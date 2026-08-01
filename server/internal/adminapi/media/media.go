// Package media converts and stores images uploaded to the admin API.
// Static images are re-encoded to JPEG, animated ones (GIF/WEBP) are handed to
// ffmpeg when available, and everything is downscaled so the shorter side is at
// most 1080px.
package media

import (
	"bytes"
	"context"
	"fmt"
	"image"
	"image/jpeg"
	"image/png"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"time"

	"ChillHub/server/internal/adminutil"
)

// Decoding bounds.
//
// A decoder allocates width*height*4 bytes for the pixel buffer before anything
// else happens, and the header that declares those numbers costs a few bytes to
// compress: a 20 KB PNG claiming 30000x30000 makes the process ask for 3.6 GB.
// The dimensions are therefore read from the header alone (image.DecodeConfig)
// and checked BEFORE the image is decoded.
const (
	// MaxImageDimension is the largest allowed width or height.
	MaxImageDimension = 8000
	// MaxImagePixels bounds width*height (~128 MiB of RGBA in the worst case).
	MaxImagePixels = 32 << 20
	// MaxImageBytes caps one uploaded image file. It lives here, next to the
	// other image bounds, so that every upload endpoint (news assets, news
	// covers, game icons) enforces the same number.
	MaxImageBytes = 32 << 20 // 32 MiB
)

// ErrImageTooLarge reports an image whose declared dimensions are refused.
var ErrImageTooLarge = fmt.Errorf("image dimensions too large")

// CheckImageBounds reads only the image header and reports whether the declared
// dimensions are safe to decode. An undecodable header is left to the caller's
// decoder to report.
func CheckImageBounds(data []byte) error {
	cfg, _, err := image.DecodeConfig(bytes.NewReader(data))
	if err != nil {
		return nil // not a format we can pre-check; the decode below will fail
	}
	if cfg.Width <= 0 || cfg.Height <= 0 {
		return ErrImageTooLarge
	}
	if cfg.Width > MaxImageDimension || cfg.Height > MaxImageDimension {
		return ErrImageTooLarge
	}
	if int64(cfg.Width)*int64(cfg.Height) > MaxImagePixels {
		return ErrImageTooLarge
	}
	return nil
}

// ProcessAndSaveAsset converts and saves image bytes into the assets directory.
//   - Chooses output extension and pipeline (static -> JPEG; animated GIF/WEBP -> WEBP if possible)
//   - Resizes so that the minimal side is 1080 if larger
//   - Returns final filename and optional meta fields (e.g., note, format)
func ProcessAndSaveAsset(base, rel, desired string, data []byte, extHint, contentType string) (string, map[string]string, error) {
	meta := map[string]string{}
	ext := strings.ToLower(strings.TrimSpace(extHint))
	if ext == "" && contentType != "" {
		if strings.Contains(contentType, "png") {
			ext = ".png"
		} else if strings.Contains(contentType, "jpeg") || strings.Contains(contentType, "jpg") {
			ext = ".jpg"
		} else if strings.Contains(contentType, "gif") {
			ext = ".gif"
		} else if strings.Contains(contentType, "webp") {
			ext = ".webp"
		}
	}

	outExt := ".jpg"
	inAnimated := false
	switch ext {
	case ".png":
		outExt = ".jpg"
	case ".jpg", ".jpeg":
		outExt = ".jpg"
	case ".gif":
		outExt = ".webp"
		inAnimated = true
	case ".webp":
		outExt = ".webp"
		inAnimated = true
	default:
		if strings.Contains(strings.ToLower(contentType), "gif") {
			outExt = ".webp"
			inAnimated = true
		} else if strings.Contains(strings.ToLower(contentType), "webp") {
			outExt = ".webp"
			inAnimated = true
		} else {
			outExt = ".jpg"
		}
	}

	if strings.TrimSpace(desired) == "" {
		desired = "image"
	}
	outName := adminutil.SanitizeFilename(desired) + outExt
	outDir := filepath.Join(base, rel)
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return "", nil, err
	}
	outPath := filepath.Join(outDir, outName)
	if !adminutil.EnsureWithin(base, outPath) {
		return "", nil, fmt.Errorf("invalid path")
	}

	if inAnimated {
		if HasFFmpeg() {
			tmpIn, err := os.CreateTemp("", "asset_in_*")
			if err != nil {
				return "", nil, err
			}
			if _, err := tmpIn.Write(data); err != nil {
				tmpIn.Close()
				return "", nil, err
			}
			tmpIn.Close()
			defer os.Remove(tmpIn.Name())
			// ffmpeg writes to a scratch file next to the destination and the
			// result is renamed into place. Letting it write outPath directly
			// meant a failed or killed transcode replaced a working asset with a
			// truncated one, in a tree that is served to the public.
			tmpOut := outPath + ".tmp-" + adminutil.GenID() + ".webp"
			defer os.Remove(tmpOut)
			scaleExpr := "scale='if(gte(min(iw,ih),1080), if(lte(iw,ih), -2, 1080), iw)':'if(gte(min(iw,ih),1080), if(lte(iw,ih), 1080, -2), ih)'"
			// Use quality 95 for lossy output and encode with libwebp preserving animation
			// Add pixel format with alpha, preset and compression level for ffmpeg 6 compatibility
			args := []string{"-y", "-i", tmpIn.Name(), "-vf", scaleExpr,
				"-c:v", "libwebp", "-lossless", "0", "-q:v", "95", "-compression_level", "4",
				"-preset", "picture", "-pix_fmt", "yuva420p", "-vsync", "0", "-loop", "0", tmpOut}
			if err := runFFmpegTranscode(args); err != nil {
				return "", nil, fmt.Errorf("ffmpeg failed: %w", err)
			}
			b, err := os.ReadFile(tmpOut)
			if err != nil {
				return "", nil, err
			}
			if err := adminutil.WriteFileAtomic(outPath, b, 0o644); err != nil {
				return "", nil, err
			}
			return outName, meta, nil
		}
		// Fallback: keep original content and original extension to avoid misleading .webp name
		// Adjust output path/name to use input extension
		origExt := ext
		if origExt == "" {
			// try guess from contentType
			if strings.Contains(strings.ToLower(contentType), "gif") {
				origExt = ".gif"
			} else if strings.Contains(strings.ToLower(contentType), "webp") {
				origExt = ".webp"
			} else {
				origExt = ".gif"
			}
		}
		// Rebuild final path/name with original extension
		outName = adminutil.SanitizeFilename(desired) + origExt
		outPath = filepath.Join(outDir, outName)
		// Atomic: the asset tree is served publicly while it is rewritten, and
		// os.WriteFile truncates first — a reader in between gets a broken image.
		if err := adminutil.WriteFileAtomic(outPath, data, 0o644); err != nil {
			return "", nil, err
		}
		log.Printf("ffmpeg not found: saved original animated image as %s", outName)
		meta["note"] = "ffmpeg not found: saved original"
		return outName, meta, nil
	}

	if err := CheckImageBounds(data); err != nil {
		return "", nil, err
	}
	img, format, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		switch ext {
		case ".png":
			if im, e2 := png.Decode(bytes.NewReader(data)); e2 == nil {
				img = im
				format = "png"
			} else {
				return "", nil, e2
			}
		case ".jpg", ".jpeg":
			if im, e2 := jpeg.Decode(bytes.NewReader(data)); e2 == nil {
				img = im
				format = "jpeg"
			} else {
				return "", nil, e2
			}
		default:
			return "", nil, fmt.Errorf("unsupported format")
		}
	}
	if format != "" {
		meta["format"] = format
	}
	outImg := ResizeToMinSide1080(img)
	// Encode into memory first, then write atomically: os.Create truncated the
	// existing asset before the encode had a chance to fail, so a failure left a
	// half-written file where a working image used to be — in a tree that is
	// served to the public.
	var buf bytes.Buffer
	if err := jpeg.Encode(&buf, outImg, &jpeg.Options{Quality: 95}); err != nil {
		return "", nil, err
	}
	if err := adminutil.WriteFileAtomic(outPath, buf.Bytes(), 0o644); err != nil {
		return "", nil, err
	}
	return outName, meta, nil
}

// ErrBlockedAddress reports a fetch aimed at the server's own network.
var ErrBlockedAddress = fmt.Errorf("address not allowed")

// blockedIP reports whether an address belongs to the infrastructure rather
// than the public internet.
//
// "Fetch this URL for me" runs inside the server's network: without this check
// an admin panel field reaches the loopback interface (the admin API itself, on
// :55777, and any other service bound to localhost), the private LAN and the
// cloud metadata endpoint at 169.254.169.254 — and the response is then stored
// under the publicly served /assets/ tree.
func blockedIP(ip net.IP) bool {
	if ip == nil {
		return true
	}
	if ip.IsLoopback() || ip.IsUnspecified() || ip.IsLinkLocalUnicast() ||
		ip.IsLinkLocalMulticast() || ip.IsInterfaceLocalMulticast() || ip.IsMulticast() {
		return true
	}
	if ip.IsPrivate() { // 10/8, 172.16/12, 192.168/16, fc00::/7
		return true
	}
	if v4 := ip.To4(); v4 != nil {
		// 100.64.0.0/10 (carrier-grade NAT) and 192.0.0.0/24 (IETF protocol
		// assignments) are not covered by IsPrivate.
		if v4[0] == 100 && v4[1] >= 64 && v4[1] <= 127 {
			return true
		}
		if v4[0] == 192 && v4[1] == 0 && v4[2] == 0 {
			return true
		}
	}
	return false
}

// safeDialer refuses to connect to a blocked address. The check runs per dial,
// so it also covers redirects and a DNS answer that changes between the lookup
// and the connection.
func safeDialer() *net.Dialer {
	d := &net.Dialer{Timeout: 10 * time.Second}
	d.Control = func(network, address string, _ syscall.RawConn) error {
		host, _, err := net.SplitHostPort(address)
		if err != nil {
			return ErrBlockedAddress
		}
		if blockedIP(net.ParseIP(host)) {
			return ErrBlockedAddress
		}
		return nil
	}
	return d
}

// DownloadURL fetches an http(s) URL, capped at 50 MiB, and returns the body
// together with the reported Content-Type. Addresses inside the server's own
// networks are refused; see blockedIP.
func DownloadURL(u string) ([]byte, string, error) {
	pu, err := url.Parse(strings.TrimSpace(u))
	if err != nil {
		return nil, "", err
	}
	if pu.Scheme != "http" && pu.Scheme != "https" {
		return nil, "", fmt.Errorf("unsupported scheme")
	}
	if pu.Hostname() == "" {
		return nil, "", ErrBlockedAddress
	}
	// Reject a literal private address up front so the error is precise; the
	// dialer's Control catches everything that only resolves to one later.
	if ip := net.ParseIP(pu.Hostname()); ip != nil && blockedIP(ip) {
		return nil, "", ErrBlockedAddress
	}
	req, _ := http.NewRequest("GET", pu.String(), nil)
	req.Header.Set("User-Agent", "ChillHub-Admin/1.0")
	client := &http.Client{
		Timeout:   20 * time.Second,
		Transport: &http.Transport{DialContext: safeDialer().DialContext},
		CheckRedirect: func(r *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return fmt.Errorf("too many redirects")
			}
			if r.URL.Scheme != "http" && r.URL.Scheme != "https" {
				return fmt.Errorf("unsupported scheme")
			}
			return nil
		},
	}
	resp, err := client.Do(req)
	if err != nil {
		return nil, "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, resp.Header.Get("Content-Type"), fmt.Errorf("http %d", resp.StatusCode)
	}
	// limit 50MB
	const max = 50 << 20
	r := io.LimitReader(resp.Body, max+1)
	b, err := io.ReadAll(r)
	if err != nil {
		return nil, resp.Header.Get("Content-Type"), err
	}
	if int64(len(b)) > max {
		return nil, resp.Header.Get("Content-Type"), fmt.Errorf("file too large")
	}
	return b, resp.Header.Get("Content-Type"), nil
}

// ResizeToMinSide1080 downscales img so that its shorter side is 1080px.
// Images that are already small enough are returned unchanged.
func ResizeToMinSide1080(img image.Image) image.Image {
	w := img.Bounds().Dx()
	h := img.Bounds().Dy()
	min := w
	if h < min {
		min = h
	}
	if min <= 1080 {
		return img
	}
	// scale so that min side becomes 1080
	scale := float64(1080) / float64(min)
	newW := int(float64(w) * scale)
	newH := int(float64(h) * scale)
	dst := image.NewRGBA(image.Rect(0, 0, newW, newH))
	// simple and dependency-free resizing using nearest neighbor sampling
	// note: for higher quality, switch to x/image/draw.ApproxBiLinear
	for y := 0; y < newH; y++ {
		for x := 0; x < newW; x++ {
			sx := int(float64(x) / scale)
			sy := int(float64(y) / scale)
			dst.Set(x, y, img.At(sx, sy))
		}
	}
	return dst
}

// FFmpegPath resolves the ffmpeg executable, honouring FFMPEG_PATH.
func FFmpegPath() string {
	if p := os.Getenv("FFMPEG_PATH"); strings.TrimSpace(p) != "" {
		return p
	}
	if p, err := exec.LookPath("ffmpeg"); err == nil {
		return p
	}
	if runtime.GOOS == "windows" {
		if p, err := exec.LookPath("ffmpeg.exe"); err == nil {
			return p
		}
	}
	return ""
}

// HasFFmpeg reports whether ffmpeg is available.
func HasFFmpeg() bool { return FFmpegPath() != "" }

// ffmpegTimeout bounds one transcode.
//
// The admin server runs with WriteTimeout deliberately set to 0 (multi-hour
// build uploads), so nothing else would ever stop a wedged ffmpeg: the process
// stayed alive holding CPU, memory and its input file until the box was
// restarted. Converting a single animated image is a matter of seconds; five
// minutes is a generous ceiling.
const ffmpegTimeout = 5 * time.Minute

func runFFmpegTranscode(args []string) error {
	exe := FFmpegPath()
	if exe == "" {
		return fmt.Errorf("ffmpeg not found")
	}
	ctx, cancel := context.WithTimeout(context.Background(), ffmpegTimeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, exe, args...)
	// Capture combined stdout/stderr to include in logs
	out, err := cmd.CombinedOutput()
	if ctx.Err() == context.DeadlineExceeded {
		log.Printf("ffmpeg timed out after %s exe=%q args=%q output:\n%s", ffmpegTimeout, exe, args, string(out))
		return fmt.Errorf("ffmpeg timed out after %s", ffmpegTimeout)
	}
	if err != nil {
		exitCode := 0
		if ee, ok := err.(*exec.ExitError); ok {
			exitCode = ee.ExitCode()
		}
		log.Printf("ffmpeg failed (code=%d) exe=%q args=%q output:\n%s", exitCode, exe, args, string(out))
		return fmt.Errorf("ffmpeg exited with code %d", exitCode)
	}
	log.Printf("ffmpeg succeeded exe=%q args=%q output:\n%s", exe, args, string(out))
	return nil
}

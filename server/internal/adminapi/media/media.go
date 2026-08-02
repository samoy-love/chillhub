// Package media converts and stores images uploaded to the admin API.
// Static images are re-encoded to JPEG, animated ones (GIF/WEBP) are handed to
// ffmpeg when available, and everything is downscaled so the shorter side is at
// most 1080px.
package media

import (
	"bytes"
	"context"
	"errors"
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

// The errors this package reports. They are sentinels rather than one-off
// fmt.Errorf values so that a caller can tell them apart with errors.Is; the
// admin API surfaces several of them to the panel verbatim, so the wording is
// part of the contract.
var (
	// ErrImageTooLarge reports an image whose declared dimensions are refused.
	ErrImageTooLarge = errors.New("image dimensions too large")
	// ErrBlockedAddress reports a fetch aimed at the server's own network.
	ErrBlockedAddress = errors.New("address not allowed")

	errInvalidPath       = errors.New("invalid path")
	errUnsupportedFormat = errors.New("unsupported format")
	errUnsupportedScheme = errors.New("unsupported scheme")
	errTooManyRedirects  = errors.New("too many redirects")
	errFileTooLarge      = errors.New("file too large")
	errHTTPStatus        = errors.New("http")
	errFFmpegMissing     = errors.New("ffmpeg not found")
	errFFmpegTimedOut    = errors.New("ffmpeg timed out")
	errFFmpegExit        = errors.New("ffmpeg exited with code")
)

// CheckImageBounds reads only the image header and reports whether the declared
// dimensions are safe to decode. An undecodable header is left to the caller's
// decoder to report.
func CheckImageBounds(data []byte) error {
	cfg, _, err := image.DecodeConfig(bytes.NewReader(data))
	if err == nil {
		switch {
		case cfg.Width <= 0 || cfg.Height <= 0:
			return ErrImageTooLarge
		case cfg.Width > MaxImageDimension || cfg.Height > MaxImageDimension:
			return ErrImageTooLarge
		case int64(cfg.Width)*int64(cfg.Height) > MaxImagePixels:
			return ErrImageTooLarge
		}
	}
	// Not a format we can pre-check: the decode that follows will fail on it.
	return nil
}

// sourceExt resolves the source extension from the client's hint, falling back
// to the reported Content-Type when the upload carried no filename.
func sourceExt(extHint, contentType string) string {
	ext := strings.ToLower(strings.TrimSpace(extHint))
	if ext != "" || contentType == "" {
		return ext
	}
	switch {
	case strings.Contains(contentType, "png"):
		return ".png"
	case strings.Contains(contentType, "jpeg"), strings.Contains(contentType, "jpg"):
		return ".jpg"
	case strings.Contains(contentType, "gif"):
		return ".gif"
	case strings.Contains(contentType, "webp"):
		return ".webp"
	}
	return ""
}

// outputPlan picks the extension the asset is stored under and reports whether
// the source may carry animation (GIF/WEBP), which needs the ffmpeg pipeline.
func outputPlan(ext, contentType string) (string, bool) {
	switch ext {
	case ".gif", ".webp":
		return ".webp", true
	case ".png", ".jpg", ".jpeg":
		return ".jpg", false
	}
	lower := strings.ToLower(contentType)
	if strings.Contains(lower, "gif") || strings.Contains(lower, "webp") {
		return ".webp", true
	}
	return ".jpg", false
}

// originalAnimatedExt is the extension an animated source keeps when no
// transcode runs. A GIF stored under a .webp name is refused by every browser.
func originalAnimatedExt(ext, contentType string) string {
	if ext != "" {
		return ext
	}
	if strings.Contains(strings.ToLower(contentType), "webp") {
		return ".webp"
	}
	return ".gif"
}

// ProcessAndSaveAsset converts and saves image bytes into the assets directory.
//   - Chooses output extension and pipeline (static -> JPEG; animated GIF/WEBP -> WEBP if possible)
//   - Resizes so that the minimal side is 1080 if larger
//   - Returns final filename and optional meta fields (e.g., note, format)
func ProcessAndSaveAsset(base, rel, desired string, data []byte, extHint, contentType string) (string, map[string]string, error) {
	meta := map[string]string{}
	ext := sourceExt(extHint, contentType)
	outExt, animated := outputPlan(ext, contentType)

	if strings.TrimSpace(desired) == "" {
		desired = "image"
	}
	outName := adminutil.SanitizeFilename(desired) + outExt
	outDir := filepath.Join(base, rel)
	// #nosec G301 -- the asset tree is handed out under /assets/ by nginx, which
	// runs as a different user than the API; 0750 would make it unreadable.
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return "", nil, err
	}
	outPath := filepath.Join(outDir, outName)
	if !adminutil.EnsureWithin(base, outPath) {
		return "", nil, errInvalidPath
	}

	if animated {
		if HasFFmpeg() {
			if err := transcodeAnimated(data, outPath); err != nil {
				return "", nil, err
			}
			return outName, meta, nil
		}
		// Fallback: keep the original bytes under the original extension.
		outName = adminutil.SanitizeFilename(desired) + originalAnimatedExt(ext, contentType)
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

	if err := storeStatic(outPath, ext, data, meta); err != nil {
		return "", nil, err
	}
	return outName, meta, nil
}

// transcodeAnimated converts an animated source to WEBP through ffmpeg and
// stores the result at outPath.
func transcodeAnimated(data []byte, outPath string) error {
	tmpIn, err := os.CreateTemp("", "asset_in_*")
	if err != nil {
		return err
	}
	defer func() { _ = os.Remove(tmpIn.Name()) }()
	if _, err := tmpIn.Write(data); err != nil {
		_ = tmpIn.Close()
		return err
	}
	if err := tmpIn.Close(); err != nil {
		return err
	}
	// ffmpeg writes to a scratch file next to the destination and the result is
	// renamed into place. Letting it write outPath directly meant a failed or
	// killed transcode replaced a working asset with a truncated one, in a tree
	// that is served to the public.
	tmpOut := outPath + ".tmp-" + adminutil.GenID() + ".webp"
	defer func() { _ = os.Remove(tmpOut) }()
	scaleExpr := "scale='if(gte(min(iw,ih),1080), if(lte(iw,ih), -2, 1080), iw)':'if(gte(min(iw,ih),1080), if(lte(iw,ih), 1080, -2), ih)'"
	// Use quality 95 for lossy output and encode with libwebp preserving animation
	// Add pixel format with alpha, preset and compression level for ffmpeg 6 compatibility
	args := []string{"-y", "-i", tmpIn.Name(), "-vf", scaleExpr,
		"-c:v", "libwebp", "-lossless", "0", "-q:v", "95", "-compression_level", "4",
		"-preset", "picture", "-pix_fmt", "yuva420p", "-vsync", "0", "-loop", "0", tmpOut}
	if err := runFFmpegTranscode(args); err != nil {
		return fmt.Errorf("ffmpeg failed: %w", err)
	}
	// #nosec G304 -- tmpOut is built right here from outPath, which EnsureWithin
	// already confirmed is inside the asset tree, plus a generated suffix.
	b, err := os.ReadFile(tmpOut)
	if err != nil {
		return err
	}
	return adminutil.WriteFileAtomic(outPath, b, 0o644)
}

// decodeStatic decodes a still image, falling back to the decoder the extension
// names when the generic registry cannot make sense of the bytes.
func decodeStatic(data []byte, ext string) (image.Image, string, error) {
	img, format, err := image.Decode(bytes.NewReader(data))
	if err == nil {
		return img, format, nil
	}
	switch ext {
	case ".png":
		im, perr := png.Decode(bytes.NewReader(data))
		if perr != nil {
			return nil, "", perr
		}
		return im, "png", nil
	case ".jpg", ".jpeg":
		im, jerr := jpeg.Decode(bytes.NewReader(data))
		if jerr != nil {
			return nil, "", jerr
		}
		return im, "jpeg", nil
	}
	return nil, "", errUnsupportedFormat
}

// storeStatic re-encodes a still image to JPEG and writes it to outPath,
// recording the source format in meta.
func storeStatic(outPath, ext string, data []byte, meta map[string]string) error {
	if err := CheckImageBounds(data); err != nil {
		return err
	}
	img, format, err := decodeStatic(data, ext)
	if err != nil {
		return err
	}
	if format != "" {
		meta["format"] = format
	}
	// Encode into memory first, then write atomically: os.Create truncated the
	// existing asset before the encode had a chance to fail, so a failure left a
	// half-written file where a working image used to be — in a tree that is
	// served to the public.
	var buf bytes.Buffer
	if err := jpeg.Encode(&buf, ResizeToMinSide1080(img), &jpeg.Options{Quality: 95}); err != nil {
		return err
	}
	return adminutil.WriteFileAtomic(outPath, buf.Bytes(), 0o644)
}

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
	if blockedIPKind(ip) {
		return true
	}
	if v4 := ip.To4(); v4 != nil {
		return blockedIPv4Range(v4)
	}
	return false
}

// blockedIPKind covers the address classes the standard library names itself.
func blockedIPKind(ip net.IP) bool {
	switch {
	case ip.IsLoopback(), ip.IsUnspecified():
		return true
	case ip.IsLinkLocalUnicast(), ip.IsLinkLocalMulticast(), ip.IsInterfaceLocalMulticast(), ip.IsMulticast():
		return true
	case ip.IsPrivate(): // 10/8, 172.16/12, 192.168/16, fc00::/7
		return true
	}
	return false
}

// blockedIPv4Range covers the IPv4 ranges net.IP.IsPrivate does not know about.
func blockedIPv4Range(v4 net.IP) bool {
	// 100.64.0.0/10, carrier-grade NAT.
	if v4[0] == 100 && v4[1] >= 64 && v4[1] <= 127 {
		return true
	}
	// 192.0.0.0/24, IETF protocol assignments.
	return v4[0] == 192 && v4[1] == 0 && v4[2] == 0
}

// safeDialer refuses to connect to a blocked address. The check runs per dial,
// so it also covers redirects and a DNS answer that changes between the lookup
// and the connection.
func safeDialer() *net.Dialer {
	d := &net.Dialer{Timeout: 10 * time.Second}
	d.Control = func(_, address string, _ syscall.RawConn) error {
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

// maxDownloadBytes caps the body DownloadURL will buffer.
const maxDownloadBytes = 50 << 20

// parseFetchTarget validates a URL the admin asked the server to fetch.
func parseFetchTarget(u string) (*url.URL, error) {
	pu, err := url.Parse(strings.TrimSpace(u))
	if err != nil {
		return nil, err
	}
	if pu.Scheme != "http" && pu.Scheme != "https" {
		return nil, errUnsupportedScheme
	}
	if pu.Hostname() == "" {
		return nil, ErrBlockedAddress
	}
	// Reject a literal private address up front so the error is precise; the
	// dialer's Control catches everything that only resolves to one later.
	if ip := net.ParseIP(pu.Hostname()); ip != nil && blockedIP(ip) {
		return nil, ErrBlockedAddress
	}
	return pu, nil
}

// fetchClient is the client DownloadURL uses: it dials through safeDialer and
// re-checks the scheme on every redirect.
func fetchClient() *http.Client {
	return &http.Client{
		Timeout:   20 * time.Second,
		Transport: &http.Transport{DialContext: safeDialer().DialContext},
		CheckRedirect: func(r *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return errTooManyRedirects
			}
			if r.URL.Scheme != "http" && r.URL.Scheme != "https" {
				return errUnsupportedScheme
			}
			return nil
		},
	}
}

// DownloadURL fetches an http(s) URL, capped at 50 MiB, and returns the body
// together with the reported Content-Type. Addresses inside the server's own
// networks are refused; see blockedIP.
//
// The context comes from the admin request that asked for the fetch, so a
// cancelled request stops the outbound download instead of leaving it running
// against a host the caller no longer waits for.
func DownloadURL(ctx context.Context, u string) ([]byte, string, error) {
	pu, err := parseFetchTarget(u)
	if err != nil {
		return nil, "", err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, pu.String(), nil)
	if err != nil {
		return nil, "", err
	}
	req.Header.Set("User-Agent", "ChillHub-Admin/1.0")
	resp, err := fetchClient().Do(req)
	if err != nil {
		return nil, "", err
	}
	defer func() { _ = resp.Body.Close() }()
	contentType := resp.Header.Get("Content-Type")
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, contentType, fmt.Errorf("%w %d", errHTTPStatus, resp.StatusCode)
	}
	b, err := io.ReadAll(io.LimitReader(resp.Body, maxDownloadBytes+1))
	if err != nil {
		return nil, contentType, err
	}
	if int64(len(b)) > maxDownloadBytes {
		return nil, contentType, errFileTooLarge
	}
	return b, contentType, nil
}

// ResizeToMinSide1080 downscales img so that its shorter side is 1080px.
// Images that are already small enough are returned unchanged.
func ResizeToMinSide1080(img image.Image) image.Image {
	w := img.Bounds().Dx()
	h := img.Bounds().Dy()
	shorter := min(w, h)
	if shorter <= 1080 {
		return img
	}
	// scale so that min side becomes 1080
	scale := float64(1080) / float64(shorter)
	newW := int(float64(w) * scale)
	newH := int(float64(h) * scale)
	dst := image.NewRGBA(image.Rect(0, 0, newW, newH))
	// simple and dependency-free resizing using nearest neighbor sampling
	// note: for higher quality, switch to x/image/draw.ApproxBiLinear
	for y := range newH {
		for x := range newW {
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
		return errFFmpegMissing
	}
	ctx, cancel := context.WithTimeout(context.Background(), ffmpegTimeout)
	defer cancel()
	// #nosec G204 -- exe is resolved by FFmpegPath from FFMPEG_PATH or PATH, and
	// args are built by this package; nothing here comes from a request.
	cmd := exec.CommandContext(ctx, exe, args...)
	// Capture combined stdout/stderr to include in logs
	out, err := cmd.CombinedOutput()
	if errors.Is(ctx.Err(), context.DeadlineExceeded) {
		log.Printf("ffmpeg timed out after %s exe=%q args=%q output:\n%s", ffmpegTimeout, exe, args, string(out))
		return fmt.Errorf("%w after %s", errFFmpegTimedOut, ffmpegTimeout)
	}
	if err != nil {
		exitCode := 0
		var ee *exec.ExitError
		if errors.As(err, &ee) {
			exitCode = ee.ExitCode()
		}
		log.Printf("ffmpeg failed (code=%d) exe=%q args=%q output:\n%s", exitCode, exe, args, string(out))
		return fmt.Errorf("%w %d", errFFmpegExit, exitCode)
	}
	log.Printf("ffmpeg succeeded exe=%q args=%q output:\n%s", exe, args, string(out))
	return nil
}

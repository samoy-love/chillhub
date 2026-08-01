// Package media converts and stores images uploaded to the admin API.
// Static images are re-encoded to JPEG, animated ones (GIF/WEBP) are handed to
// ffmpeg when available, and everything is downscaled so the shorter side is at
// most 1080px.
package media

import (
	"bytes"
	"fmt"
	"image"
	"image/jpeg"
	"image/png"
	"io"
	"log"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
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
			scaleExpr := "scale='if(gte(min(iw,ih),1080), if(lte(iw,ih), -2, 1080), iw)':'if(gte(min(iw,ih),1080), if(lte(iw,ih), 1080, -2), ih)'"
			// Use quality 95 for lossy output and encode with libwebp preserving animation
			// Add pixel format with alpha, preset and compression level for ffmpeg 6 compatibility
			args := []string{"-y", "-i", tmpIn.Name(), "-vf", scaleExpr,
				"-c:v", "libwebp", "-lossless", "0", "-q:v", "95", "-compression_level", "4",
				"-preset", "picture", "-pix_fmt", "yuva420p", "-vsync", "0", "-loop", "0", outPath}
			if err := runFFmpegTranscode(args); err != nil {
				os.Remove(tmpIn.Name())
				return "", nil, fmt.Errorf("ffmpeg failed: %w", err)
			}
			os.Remove(tmpIn.Name())
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
		if err := os.WriteFile(outPath, data, 0o644); err != nil {
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
	out, err := os.Create(outPath)
	if err != nil {
		return "", nil, err
	}
	defer out.Close()
	if err := jpeg.Encode(out, outImg, &jpeg.Options{Quality: 95}); err != nil {
		return "", nil, err
	}
	return outName, meta, nil
}

// DownloadURL fetches an http(s) URL, capped at 50 MiB, and returns the body
// together with the reported Content-Type.
func DownloadURL(u string) ([]byte, string, error) {
	pu, err := url.Parse(strings.TrimSpace(u))
	if err != nil {
		return nil, "", err
	}
	if pu.Scheme != "http" && pu.Scheme != "https" {
		return nil, "", fmt.Errorf("unsupported scheme")
	}
	req, _ := http.NewRequest("GET", pu.String(), nil)
	req.Header.Set("User-Agent", "ChillHub-Admin/1.0")
	client := &http.Client{Timeout: 20 * time.Second}
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

func runFFmpegTranscode(args []string) error {
	exe := FFmpegPath()
	if exe == "" {
		return fmt.Errorf("ffmpeg not found")
	}
	cmd := exec.Command(exe, args...)
	// Capture combined stdout/stderr to include in logs
	out, err := cmd.CombinedOutput()
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

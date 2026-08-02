package media

import (
	"bytes"
	"image"
	"image/jpeg"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// readOutput returns the bytes of the asset the pipeline reported writing.
func readOutput(t *testing.T, base, rel, name string) []byte {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(base, rel, name))
	if err != nil {
		t.Fatalf("the pipeline reported %q but nothing is there: %v", name, err)
	}
	return b
}

// A PNG is re-encoded to JPEG: the asset tree is served to every launcher, and
// storing originals meant multi-megabyte screenshots on the news page.
func TestPNGIsReEncodedToJPEG(t *testing.T) {
	base := t.TempDir()
	name, meta, err := ProcessAndSaveAsset(base, "assets", "снимок", smallPNG(t, 64, 48), ".png", "")
	if err != nil {
		t.Fatalf("ProcessAndSaveAsset: %v", err)
	}
	if !strings.HasSuffix(name, ".jpg") {
		t.Errorf("output is %q, want a .jpg", name)
	}
	if meta["format"] != "png" {
		t.Errorf("meta lost the source format: %+v", meta)
	}
	if _, err := jpeg.Decode(bytes.NewReader(readOutput(t, base, "assets", name))); err != nil {
		t.Fatalf("the stored asset is not a decodable JPEG: %v", err)
	}
}

// With no extension hint the Content-Type decides. Browsers do not always send a
// filename with the part, so this is the normal path for drag-and-drop uploads.
func TestExtensionIsGuessedFromContentType(t *testing.T) {
	base := t.TempDir()
	for _, ct := range []string{"image/png", "image/jpeg", "application/octet-stream", ""} {
		name, _, err := ProcessAndSaveAsset(base, "assets", "no-ext", smallPNG(t, 8, 8), "", ct)
		if err != nil {
			t.Fatalf("Content-Type %q: %v", ct, err)
		}
		if !strings.HasSuffix(name, ".jpg") {
			t.Errorf("Content-Type %q produced %q, want a .jpg", ct, name)
		}
	}
}

// Every non-ASCII rune used to become '_', so any two Cyrillic names of the same
// length landed on the same file: the second upload overwrote the first through
// WriteFileAtomic, and the post that linked the first image started showing the
// second one with no error anywhere.
func TestCyrillicNamesDoNotOverwriteEachOther(t *testing.T) {
	base := t.TempDir()
	first, _, err := ProcessAndSaveAsset(base, "assets", "скриншот", smallPNG(t, 64, 48), ".png", "")
	if err != nil {
		t.Fatalf("ProcessAndSaveAsset: %v", err)
	}
	second, _, err := ProcessAndSaveAsset(base, "assets", "картинка", smallPNG(t, 32, 32), ".png", "")
	if err != nil {
		t.Fatalf("ProcessAndSaveAsset: %v", err)
	}
	if first == second {
		t.Fatalf("both uploads were stored as %q; one of them is gone", first)
	}
	// Both files must still be on disk, and the first must still be the first.
	img, err := jpeg.Decode(bytes.NewReader(readOutput(t, base, "assets", first)))
	if err != nil {
		t.Fatalf("the first asset is not readable: %v", err)
	}
	if got := img.Bounds().Dx(); got != 64 {
		t.Errorf("%q is %dpx wide, want the 64px image that was uploaded under that name", first, got)
	}
	readOutput(t, base, "assets", second)
}

// The stored name ends up in a /assets/ URL served by nginx, so it must be
// ASCII and URL-safe whatever alphabet the admin's file came from.
func TestStoredNamesAreASCIIAndURLSafe(t *testing.T) {
	base := t.TempDir()
	for _, desired := range []string{"скриншот", "Обложка", "文件", "🙂", "ъь"} {
		name, _, err := ProcessAndSaveAsset(base, "assets", desired, smallPNG(t, 8, 8), ".png", "")
		if err != nil {
			t.Fatalf("desired %q: %v", desired, err)
		}
		for _, r := range name {
			if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '.' || r == '-' || r == '_' {
				continue
			}
			t.Fatalf("desired %q produced %q, which is not URL-safe ASCII", desired, name)
		}
		if strings.TrimSuffix(name, ".jpg") == "" {
			t.Fatalf("desired %q produced the nameless file %q", desired, name)
		}
		readOutput(t, base, "assets", name)
	}
}

// Re-uploading the same picture must replace it: the URL is already published in
// a news post, so a fresh name each time would leave the post on the old file.
func TestSameNameReplacesTheAsset(t *testing.T) {
	base := t.TempDir()
	first, _, err := ProcessAndSaveAsset(base, "assets", "скриншот", smallPNG(t, 64, 48), ".png", "")
	if err != nil {
		t.Fatalf("ProcessAndSaveAsset: %v", err)
	}
	second, _, err := ProcessAndSaveAsset(base, "assets", "скриншот", smallPNG(t, 32, 32), ".png", "")
	if err != nil {
		t.Fatalf("ProcessAndSaveAsset: %v", err)
	}
	if first != second {
		t.Fatalf("the same source name produced %q and then %q", first, second)
	}
	entries, err := os.ReadDir(filepath.Join(base, "assets"))
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != 1 {
		t.Fatalf("two uploads of the same name left %d files", len(entries))
	}
}

// An ASCII name still maps onto itself: every asset stored before this changed
// is addressed by that name from published posts.
func TestASCIINamesAreStoredUnchanged(t *testing.T) {
	base := t.TempDir()
	for desired, want := range map[string]string{
		"screenshot": "screenshot.jpg",
		"cover-1":    "cover-1.jpg",
		"my_file.2":  "my_file.2.jpg",
	} {
		name, _, err := ProcessAndSaveAsset(base, "assets", desired, smallPNG(t, 8, 8), ".png", "")
		if err != nil {
			t.Fatalf("desired %q: %v", desired, err)
		}
		if name != want {
			t.Errorf("desired %q was stored as %q, want %q", desired, name, want)
		}
	}
}

// The desired name comes from the admin's form field and lands on disk. A
// traversal there would write outside the asset tree, which nginx serves.
func TestDesiredNameCannotEscapeTheAssetTree(t *testing.T) {
	base := t.TempDir()
	for _, desired := range []string{"../escape", "../../etc/passwd", `..\escape`, "a/b/c"} {
		name, _, err := ProcessAndSaveAsset(base, "assets", desired, smallPNG(t, 8, 8), ".png", "")
		if err != nil {
			continue // refused outright is also a correct answer
		}
		// Separators are what makes a name a path; ".." inside one flat segment
		// (".._escape.jpg") is harmless and is what the sanitiser produces.
		if strings.ContainsAny(name, `/\`) {
			t.Errorf("desired %q produced the path-bearing name %q", desired, name)
		}
		full := filepath.Join(base, "assets", name)
		if rel, err := filepath.Rel(base, full); err != nil || strings.HasPrefix(rel, "..") {
			t.Errorf("desired %q wrote to %q, outside %q", desired, full, base)
		}
	}
	// Nothing may exist next to the base directory.
	if _, err := os.Stat(filepath.Join(filepath.Dir(base), "escape.jpg")); err == nil {
		t.Fatal("an asset was written outside the base directory")
	}
}

// An empty name must still produce a file: the upload succeeded, and a nameless
// asset would be unreachable.
func TestEmptyDesiredNameStillProducesAFile(t *testing.T) {
	base := t.TempDir()
	for _, desired := range []string{"", "   "} {
		name, _, err := ProcessAndSaveAsset(base, "assets", desired, smallPNG(t, 8, 8), ".png", "")
		if err != nil {
			t.Fatalf("desired %q: %v", desired, err)
		}
		if strings.TrimSuffix(name, ".jpg") == "" {
			t.Fatalf("desired %q produced the nameless file %q", desired, name)
		}
		readOutput(t, base, "assets", name)
	}
}

// Undecodable bytes must be an error, not a zero-length file in the public tree.
func TestGarbageBytesAreRefusedWithoutWritingAFile(t *testing.T) {
	base := t.TempDir()
	if _, _, err := ProcessAndSaveAsset(base, "assets", "плохой", []byte("это не картинка"), ".png", ""); err == nil {
		t.Fatal("garbage was accepted as an image")
	}
	entries, err := os.ReadDir(filepath.Join(base, "assets"))
	if err == nil && len(entries) > 0 {
		t.Fatalf("a rejected upload left %d files behind", len(entries))
	}
}

// Without ffmpeg an animated image is stored as-is under its ORIGINAL extension.
// Naming a GIF ".webp" would make every browser refuse to render it.
func TestAnimatedFallbackKeepsTheOriginalExtension(t *testing.T) {
	// exec.LookPath cannot find anything with an empty PATH, so this exercises the
	// no-ffmpeg branch on a machine that does have ffmpeg installed.
	t.Setenv("FFMPEG_PATH", "")
	t.Setenv("PATH", "")
	if HasFFmpeg() {
		t.Skip("ffmpeg is still resolvable; the no-ffmpeg branch cannot be forced here")
	}

	base := t.TempDir()
	gif := []byte("GIF89a исходные байты")
	name, meta, err := ProcessAndSaveAsset(base, "assets", "анимация", gif, ".gif", "")
	if err != nil {
		t.Fatalf("ProcessAndSaveAsset: %v", err)
	}
	if !strings.HasSuffix(name, ".gif") {
		t.Fatalf("output is %q; a GIF stored under a .webp name will not render", name)
	}
	if !bytes.Equal(readOutput(t, base, "assets", name), gif) {
		t.Error("the original bytes were altered although no transcode ran")
	}
	if meta["note"] == "" {
		t.Error("the fallback is not reported in meta; the admin has no way to learn the file was not converted")
	}
}

// The animated extension is derived from the Content-Type when no hint is given.
func TestAnimatedContentTypeIsRecognisedWithoutAHint(t *testing.T) {
	t.Setenv("FFMPEG_PATH", "")
	t.Setenv("PATH", "")
	if HasFFmpeg() {
		t.Skip("ffmpeg is still resolvable")
	}
	base := t.TempDir()
	name, _, err := ProcessAndSaveAsset(base, "assets", "анимация", []byte("GIF89a"), "", "image/gif")
	if err != nil {
		t.Fatalf("ProcessAndSaveAsset: %v", err)
	}
	if !strings.HasSuffix(name, ".gif") {
		t.Fatalf("image/gif produced %q", name)
	}
}

// A broken ffmpeg must fail loudly rather than leave a truncated asset in place
// of the working one — the tree is served publicly while it is rewritten.
func TestFailedTranscodeLeavesNoPartialAsset(t *testing.T) {
	t.Setenv("FFMPEG_PATH", filepath.Join(t.TempDir(), "нет-такого-ffmpeg"))
	if !HasFFmpeg() {
		t.Skip("FFMPEG_PATH is not honoured here")
	}

	base := t.TempDir()
	if _, _, err := ProcessAndSaveAsset(base, "assets", "анимация", []byte("GIF89a"), ".gif", ""); err == nil {
		t.Fatal("a failed transcode was reported as success")
	}
	entries, err := os.ReadDir(filepath.Join(base, "assets"))
	if err == nil {
		for _, e := range entries {
			t.Errorf("a failed transcode left %q behind", e.Name())
		}
	}
}

// FFMPEG_PATH wins over PATH: that is how the deploy pins a specific build.
func TestFFmpegPathHonoursTheEnvironmentOverride(t *testing.T) {
	t.Setenv("FFMPEG_PATH", "/opt/custom/ffmpeg")
	if got := FFmpegPath(); got != "/opt/custom/ffmpeg" {
		t.Fatalf("FFmpegPath = %q, want the configured override", got)
	}
	if !HasFFmpeg() {
		t.Fatal("HasFFmpeg disagrees with FFmpegPath")
	}
}

// Images at or below the target are returned untouched: re-sampling a 1080p
// screenshot to 1080p only costs quality.
func TestSmallImagesAreNotResampled(t *testing.T) {
	for _, r := range []image.Rectangle{
		image.Rect(0, 0, 100, 100),
		image.Rect(0, 0, 1920, 1080), // shorter side exactly at the target
		image.Rect(0, 0, 4000, 500),  // huge but thin
	} {
		src := image.NewRGBA(r)
		if got := ResizeToMinSide1080(src); got != image.Image(src) {
			t.Errorf("%v was resampled: %v", r, got.Bounds())
		}
	}
}

// Larger images are downscaled to a 1080px shorter side with the aspect ratio kept.
func TestLargeImagesAreDownscaledKeepingAspect(t *testing.T) {
	src := image.NewRGBA(image.Rect(0, 0, 4000, 2000))
	got := ResizeToMinSide1080(src).Bounds()

	if got.Dy() != 1080 {
		t.Fatalf("shorter side is %d, want 1080", got.Dy())
	}
	wantW := 2160 // 4000 * 1080/2000
	if got.Dx() < wantW-2 || got.Dx() > wantW+2 {
		t.Fatalf("width is %d, want about %d — the aspect ratio was not kept", got.Dx(), wantW)
	}
}

// Portrait orientation must be handled by the shorter side too, not by width.
func TestPortraitImagesUseTheShorterSide(t *testing.T) {
	got := ResizeToMinSide1080(image.NewRGBA(image.Rect(0, 0, 2000, 4000))).Bounds()
	if got.Dx() != 1080 {
		t.Fatalf("shorter side is %d, want 1080", got.Dx())
	}
}

// A header that cannot be parsed is left to the decoder to reject: refusing here
// would break every format the stdlib registry does not know.
func TestUnparseableHeaderIsNotRejectedByBoundsCheck(t *testing.T) {
	if err := CheckImageBounds([]byte("вовсе не изображение")); err != nil {
		t.Fatalf("CheckImageBounds refused an unknown format outright: %v", err)
	}
	if err := CheckImageBounds(nil); err != nil {
		t.Fatalf("CheckImageBounds refused empty input: %v", err)
	}
}

// A URL that does not parse, or has no host, must not reach the network.
func TestDownloadURLRefusesMalformedInput(t *testing.T) {
	for _, u := range []string{"", "   ", "http://", "https:///path", "не ссылка", "://x"} {
		if _, _, err := DownloadURL(u); err == nil {
			t.Errorf("DownloadURL(%q) succeeded", u)
		}
	}
}

package games

import (
	"bytes"
	"image"
	"image/color"
	"image/png"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

func pngBytes(t *testing.T, w, h int) []byte {
	t.Helper()
	img := image.NewRGBA(image.Rect(0, 0, w, h))
	img.Set(0, 0, color.RGBA{R: 1, G: 2, B: 3, A: 255})
	var buf bytes.Buffer
	if err := png.Encode(&buf, img); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func iconRequest(t *testing.T, gid string, data []byte) *http.Request {
	t.Helper()
	var body bytes.Buffer
	mw := multipart.NewWriter(&body)
	_ = mw.WriteField("gameId", gid)
	fw, err := mw.CreateFormFile("file", "icon.png")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := fw.Write(data); err != nil {
		t.Fatal(err)
	}
	if err := mw.Close(); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/games/icon/upload", &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// IconUpload used to run os.MkdirAll on the unvalidated gameId and only then
// check EnsureWithin, so a traversal id created directories outside the tree.
func TestIconUploadRejectsUnsafeGameIDBeforeMkdir(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	for _, gid := range []string{"../evil", "a/b", `..\evil`} {
		w := httptest.NewRecorder()
		h.IconUpload(w, iconRequest(t, gid, pngBytes(t, 4, 4)))
		if w.Code != http.StatusBadRequest {
			t.Errorf("IconUpload(%q) = %d, want 400", gid, w.Code)
		}
	}
	if _, err := os.Stat(filepath.Join(root, "evil")); err == nil {
		t.Fatal("IconUpload created a directory outside the manifests tree")
	}
	if entries, err := os.ReadDir(filepath.Join(root, "manifests")); err == nil {
		for _, e := range entries {
			t.Fatalf("IconUpload created %q for an invalid gameId", e.Name())
		}
	}
}

func TestIconUploadAcceptsValidGameID(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	h.IconUpload(w, iconRequest(t, "my_game-1", pngBytes(t, 8, 8)))
	if w.Code != http.StatusOK {
		t.Fatalf("IconUpload = %d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "manifests", "my_game-1", "icon.png")); err != nil {
		t.Fatalf("icon not saved: %v", err)
	}
}

package games

import (
	"bytes"
	"encoding/binary"
	"hash/crc32"
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

// A tiny PNG declaring enormous dimensions must be refused before the decoder
// allocates its pixel buffer.
func TestIconUploadRejectsImageBomb(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	h.IconUpload(w, iconRequest(t, "game", pngBombBytes(t, 30000, 30000)))
	if w.Code != http.StatusBadRequest {
		t.Fatalf("IconUpload accepted an image bomb: %d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "manifests", "game", "icon.png")); err == nil {
		t.Fatal("the bomb was written to disk")
	}
}

// pngBombBytes builds a valid PNG header declaring huge dimensions.
func pngBombBytes(t *testing.T, w, h uint32) []byte {
	t.Helper()
	var buf bytes.Buffer
	buf.Write([]byte{0x89, 'P', 'N', 'G', 0x0d, 0x0a, 0x1a, 0x0a})
	var ihdr bytes.Buffer
	_ = binary.Write(&ihdr, binary.BigEndian, w)
	_ = binary.Write(&ihdr, binary.BigEndian, h)
	ihdr.Write([]byte{8, 6, 0, 0, 0})
	chunk := func(typ string, data []byte) {
		_ = binary.Write(&buf, binary.BigEndian, uint32(len(data)))
		buf.WriteString(typ)
		buf.Write(data)
		c := crc32.NewIEEE()
		c.Write([]byte(typ))
		c.Write(data)
		_ = binary.Write(&buf, binary.BigEndian, c.Sum32())
	}
	chunk("IHDR", ihdr.Bytes())
	chunk("IDAT", []byte{0x78, 0x9c, 0x01, 0x00, 0x00, 0xff, 0xff, 0x00, 0x00, 0x00, 0x01})
	chunk("IEND", nil)
	return buf.Bytes()
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

package news

import (
	"bytes"
	"io"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"testing"
)

// multipartFile builds a multipart body with one "file" part of n bytes.
func multipartFile(t *testing.T, name string, n int) (*bytes.Buffer, string) {
	t.Helper()
	var body bytes.Buffer
	mw := multipart.NewWriter(&body)
	fw, err := mw.CreateFormFile("file", name)
	if err != nil {
		t.Fatal(err)
	}
	chunk := bytes.Repeat([]byte("A"), 1<<20)
	for written := 0; written < n; written += len(chunk) {
		size := len(chunk)
		if rem := n - written; rem < size {
			size = rem
		}
		if _, err := fw.Write(chunk[:size]); err != nil {
			t.Fatal(err)
		}
	}
	if err := mw.Close(); err != nil {
		t.Fatal(err)
	}
	return &body, mw.FormDataContentType()
}

// An oversized image must be refused instead of being buffered whole: both
// endpoints used to accept an unbounded body (64 MiB / 32 MiB parse windows and
// an unlimited io.ReadAll / io.Copy behind them).
func TestAssetsUploadRejectsOversizedBody(t *testing.T) {
	h := New(t.TempDir())
	body, ct := multipartFile(t, "big.png", MaxImageBytes+(4<<20))
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/news/assets/upload", body)
	req.Header.Set("Content-Type", ct)
	w := httptest.NewRecorder()
	h.AssetsUpload(w, req)
	if w.Code == http.StatusOK {
		t.Fatalf("oversized asset upload was accepted (%d)", w.Code)
	}
	if w.Code != http.StatusBadRequest && w.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("unexpected status %d: %s", w.Code, w.Body.String())
	}
}

func TestUploadCoverRejectsOversizedBody(t *testing.T) {
	h := New(t.TempDir())
	body, ct := multipartFile(t, "big.png", MaxImageBytes+(4<<20))
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/news/uploadCover", body)
	req.Header.Set("Content-Type", ct)
	w := httptest.NewRecorder()
	h.UploadCover(w, req)
	if w.Code == http.StatusOK {
		t.Fatalf("oversized cover upload was accepted (%d)", w.Code)
	}
	if w.Code != http.StatusBadRequest && w.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("unexpected status %d: %s", w.Code, w.Body.String())
	}
}

// A normal-sized cover still works.
func TestUploadCoverAcceptsSmallImage(t *testing.T) {
	h := New(t.TempDir())
	body, ct := multipartFile(t, "small.png", 1<<10)
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/news/uploadCover", body)
	req.Header.Set("Content-Type", ct)
	w := httptest.NewRecorder()
	h.UploadCover(w, req)
	if w.Code != http.StatusOK {
		b, _ := io.ReadAll(w.Body)
		t.Fatalf("small cover rejected: %d %s", w.Code, string(b))
	}
}

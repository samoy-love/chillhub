package builds

import (
	"bytes"
	"encoding/json"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

// kindUploadRequest is uploadRequest with a caller-chosen kind/gameId, needed
// here because uploadRequest hardcodes kind=game.
func kindUploadRequest(t *testing.T, kind, gid, ver string, zipData []byte) *http.Request {
	t.Helper()
	var body bytes.Buffer
	mw := multipart.NewWriter(&body)
	_ = mw.WriteField("kind", kind)
	_ = mw.WriteField("gameId", gid)
	_ = mw.WriteField("version", ver)
	fw, err := mw.CreateFormFile("zip", "build.zip")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := fw.Write(zipData); err != nil {
		t.Fatal(err)
	}
	if err := mw.Close(); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/upload", &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// TestLauncherReuploadUnderSameVersionRejected reproduces, at the HTTP layer,
// the 2026-08-08 incident: the same launcher version uploaded a second time
// with different content. This must now be refused, and — the part that
// actually matters — the FIRST upload's content must survive untouched: a
// client that already reads "1.3.2" as fixed content is not allowed to have
// that content silently swapped out from under it.
func TestLauncherReuploadUnderSameVersionRejected(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	w1 := httptest.NewRecorder()
	h.Upload(w1, kindUploadRequest(t, "launcher", "launcher", "1.3.2", zipBytes(t, map[string]string{
		"ChillHub.exe": "first build",
	})))
	if w1.Code != http.StatusOK {
		t.Fatalf("first upload: %d %s", w1.Code, w1.Body.String())
	}

	w2 := httptest.NewRecorder()
	h.Upload(w2, kindUploadRequest(t, "launcher", "launcher", "1.3.2", zipBytes(t, map[string]string{
		"ChillHub.exe": "second, different build",
	})))
	if w2.Code != http.StatusConflict {
		t.Fatalf("second upload under the same version: got %d %s, want %d", w2.Code, w2.Body.String(), http.StatusConflict)
	}

	got, err := os.ReadFile(filepath.Join(root, "content", "launcher", "1.3.2", "files", "ChillHub.exe"))
	if err != nil {
		t.Fatalf("original content missing after rejected re-upload: %v", err)
	}
	if string(got) != "first build" {
		t.Fatalf("content = %q, want the first upload untouched (%q)", got, "first build")
	}
}

// TestGameReuploadUnderSameVersionStillAllowed pins the boundary of the guard:
// it is scoped to gid=="launcher" only. Games keep the pre-existing "same
// version, new content" workflow — the 2026-08-08 incident was specific to
// self-update comparing version strings, and nothing about that applies here.
func TestGameReuploadUnderSameVersionStillAllowed(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	w1 := httptest.NewRecorder()
	h.Upload(w1, kindUploadRequest(t, "game", "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "first"})))
	if w1.Code != http.StatusOK {
		t.Fatalf("first upload: %d %s", w1.Code, w1.Body.String())
	}

	w2 := httptest.NewRecorder()
	h.Upload(w2, kindUploadRequest(t, "game", "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "second"})))
	if w2.Code != http.StatusOK {
		t.Fatalf("re-upload of a game under the same version must still succeed: %d %s", w2.Code, w2.Body.String())
	}
	got, err := os.ReadFile(filepath.Join(root, "content", "game", "1.0.0", "files", "a.txt"))
	if err != nil {
		t.Fatalf("read republished content: %v", err)
	}
	if string(got) != "second" {
		t.Fatalf("content = %q, want the re-upload to have replaced it (%q)", got, "second")
	}
}

// TestUploadInitRefusesAlreadyPublishedLauncherVersion checks the chunked
// entry point separately: it has its own copy of the guard (see
// launcherVersionAlreadyPublished's call sites) so that a chunked re-upload
// is refused before the client spends any bandwidth on it, not just after.
func TestUploadInitRefusesAlreadyPublishedLauncherVersion(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }

	w1 := httptest.NewRecorder()
	h.Upload(w1, kindUploadRequest(t, "launcher", "launcher", "1.3.2", zipBytes(t, map[string]string{
		"ChillHub.exe": "already published",
	})))
	if w1.Code != http.StatusOK {
		t.Fatalf("seed upload: %d %s", w1.Code, w1.Body.String())
	}

	initBody, err := json.Marshal(struct {
		Kind      string `json:"kind"`
		GameID    string `json:"gameId"`
		Version   string `json:"version"`
		ZipName   string `json:"zipName"`
		TotalSize int64  `json:"totalSize"`
	}{"launcher", "launcher", "1.3.2", "build.zip", 1024})
	if err != nil {
		t.Fatalf("marshal init body: %v", err)
	}
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/upload/init", bytes.NewReader(initBody))
	w2 := httptest.NewRecorder()
	h.UploadInit(w2, req)
	if w2.Code != http.StatusConflict {
		t.Fatalf("UploadInit for an already-published launcher version: got %d %s, want %d", w2.Code, w2.Body.String(), http.StatusConflict)
	}
}

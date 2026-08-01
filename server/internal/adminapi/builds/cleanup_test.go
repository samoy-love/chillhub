package builds

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// seedUpload creates an upload directory with a meta record of the given status
// and age, plus a payload file.
func seedUpload(t *testing.T, h *Handlers, id, status string, idle time.Duration) {
	t.Helper()
	if err := os.MkdirAll(h.uploadDir(id), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(h.uploadZipPartPath(id), []byte("payload"), 0o644); err != nil {
		t.Fatal(err)
	}
	m := &uploadMeta{UploadID: id, Kind: "game", GameID: "game", Version: "1.0.0", Status: status}
	if err := h.writeUploadMeta(m); err != nil {
		t.Fatal(err)
	}
	// writeUploadMeta stamps UpdatedAt with "now"; rewrite it to the wanted age.
	m.UpdatedAt = time.Now().Add(-idle).Unix()
	b, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(h.uploadMetaPath(id), b, 0o644); err != nil {
		t.Fatal(err)
	}
}

func cleanupRequest(t *testing.T, h *Handlers) *httptest.ResponseRecorder {
	t.Helper()
	w := httptest.NewRecorder()
	h.UploadCleanup(w, httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/upload/cleanup", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("cleanup: %d %s", w.Code, w.Body.String())
	}
	return w
}

// The cleanup endpoint used to os.RemoveAll everything under content/tmp, which
// destroyed an upload another admin was still streaming chunks into.
func TestUploadCleanupKeepsActiveUploads(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }

	const active = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
	const stale = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
	seedUpload(t, h, active, "uploading", time.Minute) // touched a minute ago
	seedUpload(t, h, stale, "uploading", 6*time.Hour)  // abandoned

	cleanupRequest(t, h)

	if _, err := os.Stat(h.uploadMetaPath(active)); err != nil {
		t.Fatalf("cleanup destroyed an active upload: %v", err)
	}
	if _, err := os.Stat(h.uploadZipPartPath(active)); err != nil {
		t.Fatalf("cleanup destroyed an active upload's payload: %v", err)
	}
	if _, err := os.Stat(h.uploadDir(stale)); err == nil {
		t.Fatal("abandoned upload was not removed")
	}
}

// A ZIP that some request may still be writing must not be pulled out from
// under it either.
func TestUploadCleanupKeepsFreshStreamTempZip(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }

	tmpDir := filepath.Join(root, "tmp")
	if err := os.MkdirAll(tmpDir, 0o755); err != nil {
		t.Fatal(err)
	}
	fresh := filepath.Join(tmpDir, "upload-fresh.zip")
	old := filepath.Join(tmpDir, "upload-old.zip")
	for _, p := range []string{fresh, old} {
		if err := os.WriteFile(p, []byte("zip"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	past := time.Now().Add(-6 * time.Hour)
	if err := os.Chtimes(old, past, past); err != nil {
		t.Fatal(err)
	}

	cleanupRequest(t, h)

	if _, err := os.Stat(fresh); err != nil {
		t.Errorf("cleanup removed a temp ZIP of a live request: %v", err)
	}
	if _, err := os.Stat(old); err == nil {
		t.Error("stale temp ZIP was not removed")
	}
}

// The janitor's done/processed branch was dead: nothing wrote those values, so
// a finished upload's ZIP lingered for the full 12-hour expiry. Now processing
// maintains the status and the record expires on its own schedule.
func TestJanitorExpiresRecords(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	const running = "cccccccccccccccccccccccccccccccc"
	const doneOld = "dddddddddddddddddddddddddddddddd"
	const abandoned = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
	seedUpload(t, h, running, "uploading", 30*time.Minute)
	seedUpload(t, h, doneOld, "done", 3*time.Hour)
	seedUpload(t, h, abandoned, "uploading", 13*time.Hour)

	h.sweepUploads(time.Now())

	if _, err := os.Stat(h.uploadDir(running)); err != nil {
		t.Errorf("janitor removed a live upload: %v", err)
	}
	if _, err := os.Stat(h.uploadDir(doneOld)); err == nil {
		t.Error("finished upload record was kept forever")
	}
	if _, err := os.Stat(h.uploadDir(abandoned)); err == nil {
		t.Error("abandoned upload was not expired")
	}
}

// Processing must consume the archive: it is the single largest thing on the
// volume and used to survive the full expiry window.
func TestProcessRemovesArchiveOnSuccess(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }

	id := "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f"
	if err := os.MkdirAll(h.uploadDir(id), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(h.uploadZipPath(id), zipBytes(t, map[string]string{"a.txt": "x"}), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := h.writeUploadMeta(&uploadMeta{
		UploadID: id, Kind: "game", GameID: "game", Version: "1.0.0", Status: "ready",
	}); err != nil {
		t.Fatal(err)
	}

	w := httptest.NewRecorder()
	h.UploadProcessStream(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/upload/process?uploadId="+id, nil))

	events, garbage := ndjsonEvents(t, w.Body.String())
	if len(garbage) > 0 || hasErrorEvent(events) {
		t.Fatalf("process failed: %s", w.Body.String())
	}
	if _, err := os.Stat(h.uploadZipPath(id)); err == nil {
		t.Error("the archive survived a successful publication")
	}
	m, err := h.readUploadMeta(id)
	if err != nil {
		t.Fatalf("meta lost: %v", err)
	}
	if m.Status != "done" {
		t.Errorf("status = %q, want done", m.Status)
	}
}

package builds

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ndjsonEvents parses an NDJSON body and reports the parsed events plus any
// line that is not JSON at all (i.e. plain text injected into the stream).
func ndjsonEvents(t *testing.T, body string) (events []map[string]any, garbage []string) {
	t.Helper()
	for line := range strings.SplitSeq(strings.TrimSpace(body), "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		var ev map[string]any
		if json.Unmarshal([]byte(line), &ev) != nil {
			garbage = append(garbage, line)
			continue
		}
		events = append(events, ev)
	}
	return events, garbage
}

func hasErrorEvent(events []map[string]any) bool {
	for _, ev := range events {
		if ev["type"] == "error" {
			return true
		}
	}
	return false
}

// The temporary ZIP must not survive a rejected upload: only the success path
// used to remove it.
func assertNoTempZip(t *testing.T, root string) {
	t.Helper()
	matches, _ := filepath.Glob(filepath.Join(root, "tmp", "upload-*.zip"))
	if len(matches) > 0 {
		t.Errorf("temp ZIP leaked: %v", matches)
	}
}

// A broken archive on the chunked path must also produce an error event, not a
// plain-text status line.
func TestUploadProcessStreamReportsErrorsAsEvents(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }

	id := "0123456789abcdef0123456789abcdef"
	if err := os.MkdirAll(h.uploadDir(id), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(h.uploadZipPath(id), []byte("not a zip"), 0o644); err != nil {
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
	if len(garbage) > 0 {
		t.Errorf("plain text injected into the NDJSON stream: %q", garbage)
	}
	if !hasErrorEvent(events) {
		t.Errorf("no error event: %s", w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); err == nil {
		t.Error("a failed process published a version")
	}
}

package builds

import (
	"bytes"
	"encoding/json"
	"mime/multipart"
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

// streamUploadRequest posts a multipart body where the zip part comes FIRST, so
// the handler has already emitted and flushed the zipSaved event by the time it
// validates the (missing/invalid) parameters.
func streamUploadRequest(t *testing.T, fields map[string]string, zipData []byte) *http.Request {
	t.Helper()
	var body bytes.Buffer
	mw := multipart.NewWriter(&body)
	fw, err := mw.CreateFormFile("zip", "build.zip")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := fw.Write(zipData); err != nil {
		t.Fatal(err)
	}
	for k, v := range fields {
		if err := mw.WriteField(k, v); err != nil {
			t.Fatal(err)
		}
	}
	if err := mw.Close(); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/uploadStream", &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// Once the stream has started, a rejected parameter must arrive as an error
// event. http.Error at that point cannot set a status any more and only injects
// plain text into the NDJSON body, which the client silently treats as success.
func TestUploadStreamReportsLateErrorsAsEvents(t *testing.T) {
	cases := map[string]map[string]string{
		"invalidGameID":  {"kind": "game", "gameId": "../evil", "version": "1.0.0"},
		"invalidVersion": {"kind": "game", "gameId": "game", "version": "../../x"},
		"missingVersion": {"kind": "game", "gameId": "game"},
		"missingKind":    {"gameId": "game", "version": "1.0.0"},
	}
	for name, fields := range cases {
		t.Run(name, func(t *testing.T) {
			root := t.TempDir()
			h := New(root)
			h.CurrentUser = func(*http.Request) string { return "admin" }

			w := httptest.NewRecorder()
			h.UploadStream(w, streamUploadRequest(t, fields, zipBytes(t, map[string]string{"a.txt": "x"})))

			events, garbage := ndjsonEvents(t, w.Body.String())
			if len(garbage) > 0 {
				t.Errorf("plain text injected into the NDJSON stream: %q", garbage)
			}
			if !hasErrorEvent(events) {
				t.Errorf("no {\"type\":\"error\"} event; client would read this as success: %s", w.Body.String())
			}
			for _, ev := range events {
				if ev["type"] == "done" {
					t.Error("a failed publication reported done")
				}
			}
			assertNoTempZip(t, root)
		})
	}
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

// A successful stream upload still ends with done and cleans its temp ZIP up.
func TestUploadStreamSuccess(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }

	w := httptest.NewRecorder()
	h.UploadStream(w, streamUploadRequest(t,
		map[string]string{"kind": "game", "gameId": "game", "version": "1.0.0"},
		zipBytes(t, map[string]string{"a.txt": "x"})))

	events, garbage := ndjsonEvents(t, w.Body.String())
	if len(garbage) > 0 {
		t.Fatalf("garbage in stream: %q", garbage)
	}
	if hasErrorEvent(events) {
		t.Fatalf("unexpected error event: %s", w.Body.String())
	}
	done := false
	for _, ev := range events {
		if ev["type"] == "done" {
			done = true
		}
	}
	if !done {
		t.Fatalf("no done event: %s", w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0", "files", "a.txt")); err != nil {
		t.Fatalf("build not published: %v", err)
	}
	assertNoTempZip(t, root)
}

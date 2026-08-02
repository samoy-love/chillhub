package metrics

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// /metrics/report is public and unauthenticated. When the store cannot be
// written (a full disk, bad permissions) the reply must not contain the
// absolute path of the content root.
func TestSubmitDoesNotLeakFilesystemPaths(t *testing.T) {
	root := t.TempDir()
	// Make the metrics directory impossible to create by putting a FILE where it
	// has to go; every OS then fails the MkdirAll with a path-carrying error.
	if err := os.WriteFile(filepath.Join(root, "metrics"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	h := New(root)

	w := httptest.NewRecorder()
	r := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://example.com/metrics/report",
		strings.NewReader(`{"event":"launcher_start"}`))
	h.Submit(w, r)

	if w.Code != http.StatusInternalServerError {
		t.Fatalf("expected 500, got %d (%s)", w.Code, w.Body.String())
	}
	body := w.Body.String()
	for _, needle := range []string{root, "metrics" + string(os.PathSeparator), ".jsonl"} {
		if strings.Contains(body, needle) {
			t.Errorf("response leaks %q: %s", needle, body)
		}
	}
}

package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"ChillHub/server/internal/ratelimit"
)

// withContentRoot points the package-level contentRoot at a temp dir for the
// duration of a test.
func withContentRoot(t *testing.T) string {
	t.Helper()
	old := contentRoot
	dir := t.TempDir()
	contentRoot = dir
	t.Cleanup(func() { contentRoot = old })
	return dir
}

func testRouter() http.Handler {
	return newRouter(ratelimit.New(0, time.Minute))
}

func TestGameIDIsValidatedOnPublicRoutes(t *testing.T) {
	root := withContentRoot(t)
	// The internal registry directory really exists in production.
	if err := os.MkdirAll(filepath.Join(root, "manifests", "_registry"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "manifests", "_registry", "games.json"), []byte(`{"items":[]}`), 0o644); err != nil {
		t.Fatal(err)
	}
	r := testRouter()
	long := strings.Repeat("a", 300)
	bad := []string{
		"/api/games/_registry",
		"/api/games/_registry/builds",
		"/api/games/_registry/versions/latest",
		"/api/games/" + long + "/builds",
		"/api/games/bad%20id/builds",
		"/news/games/_registry/index.json",
	}
	for _, p := range bad {
		rec := httptest.NewRecorder()
		r.ServeHTTP(rec, httptest.NewRequest(http.MethodGet, p, nil))
		if rec.Code != http.StatusNotFound {
			t.Errorf("GET %s = %d, want 404", p, rec.Code)
		}
	}
}

func TestNewsIndexNeverServesRawFile(t *testing.T) {
	root := withContentRoot(t)
	if err := os.MkdirAll(filepath.Join(root, "news"), 0o755); err != nil {
		t.Fatal(err)
	}
	// An index whose "items" key is absent used to fall through to the raw
	// bytes, leaking whatever the file contained.
	raw := `{"drafts":[{"slug":"secret","published":false}]}`
	if err := os.WriteFile(filepath.Join(root, "news", "index.json"), []byte(raw), 0o644); err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/news/index.json", nil))
	if strings.Contains(rec.Body.String(), "secret") {
		t.Fatalf("raw index leaked to the client: %s", rec.Body.String())
	}

	// Malformed JSON must not be echoed either.
	if err := os.WriteFile(filepath.Join(root, "news", "index.json"), []byte(`{"items":[`), 0o644); err != nil {
		t.Fatal(err)
	}
	rec = httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/news/index.json", nil))
	var got struct {
		Items []map[string]any `json:"items"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("response is not valid json: %v (%s)", err, rec.Body.String())
	}
	if len(got.Items) != 0 {
		t.Fatalf("want empty items, got %v", got.Items)
	}
}

func TestNewsIndexFiltersUnpublished(t *testing.T) {
	root := withContentRoot(t)
	if err := os.MkdirAll(filepath.Join(root, "news", "games", "demo"), 0o755); err != nil {
		t.Fatal(err)
	}
	body := `{"items":[{"slug":"a","published":true},{"slug":"b","published":false},{"slug":"c"}]}`
	if err := os.WriteFile(filepath.Join(root, "news", "games", "demo", "index.json"), []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/news/games/demo/index.json", nil))
	if strings.Contains(rec.Body.String(), `"b"`) {
		t.Fatalf("unpublished item served: %s", rec.Body.String())
	}
	if !strings.Contains(rec.Body.String(), `"a"`) || !strings.Contains(rec.Body.String(), `"c"`) {
		t.Fatalf("published items missing: %s", rec.Body.String())
	}
}

func TestHeadIsAllowedWhereverGetIs(t *testing.T) {
	root := withContentRoot(t)
	if err := os.MkdirAll(filepath.Join(root, "manifests", "demo"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "manifests", "demo", "latest.json"), []byte(`{"version":"1.0.0"}`), 0o644); err != nil {
		t.Fatal(err)
	}
	r := testRouter()
	paths := []string{
		"/api/games",
		"/api/games/demo",
		"/api/games/demo/versions/latest",
		"/api/games/demo/builds",
		"/api/maintenance",
		"/news/index.json",
		"/news/games/demo/index.json",
	}
	for _, p := range paths {
		rec := httptest.NewRecorder()
		r.ServeHTTP(rec, httptest.NewRequest(http.MethodHead, p, nil))
		if rec.Code == http.StatusMethodNotAllowed {
			t.Errorf("HEAD %s = 405, want it to be served like GET", p)
		}
	}
}

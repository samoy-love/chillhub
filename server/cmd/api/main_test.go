package main

import (
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

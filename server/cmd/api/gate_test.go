package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	"ChillHub/server/internal/promexp"
	"ChillHub/server/internal/ratelimit"
)

// buildsOf asks /api/games/{gid}/builds and returns the version list.
func buildsOf(t *testing.T, gid string) []string {
	t.Helper()
	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games/"+gid+"/builds", nil))
	var got struct {
		Items []string `json:"items"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("bad json %q: %v", rec.Body.String(), err)
	}
	return got.Items
}

// writeManifests creates manifests/{gid}/{version}.json for every version.
func writeManifests(t *testing.T, root, gid string, versions ...string) string {
	t.Helper()
	dir := filepath.Join(root, "manifests", gid)
	if err := os.MkdirAll(dir, 0o750); err != nil {
		t.Fatal(err)
	}
	for _, v := range versions {
		if err := os.WriteFile(filepath.Join(dir, v+".json"), []byte(`{}`), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	return dir
}

// latest.json is the publication gate. A chunked upload writes the manifest but
// no latest.json at all, and the launcher installs the newest entry of this list
// whenever the latest endpoint is silent — so serving the list unfiltered handed
// every player a build nobody activated.
func TestBuildsWithoutLatestAreNotOffered(t *testing.T) {
	root := withContentRoot(t)
	writeManifests(t, root, "demo", "1.0.0", "1.1.0", "2.0.0")

	if items := buildsOf(t, "demo"); len(items) != 0 {
		t.Fatalf("builds without latest.json = %v, want none offered", items)
	}
}

// An unreadable gate is no gate either: a truncated or versionless latest.json
// must not turn into "everything is published".
func TestBuildsWithCorruptLatestAreNotOffered(t *testing.T) {
	root := withContentRoot(t)
	dir := writeManifests(t, root, "demo", "1.0.0", "2.0.0")

	for _, body := range []string{`{"version":`, `{}`, `{"version":""}`, ``} {
		if err := os.WriteFile(filepath.Join(dir, "latest.json"), []byte(body), 0o600); err != nil {
			t.Fatal(err)
		}
		if items := buildsOf(t, "demo"); len(items) != 0 {
			t.Fatalf("latest.json %q: builds = %v, want none offered", body, items)
		}
	}
}

// A registry that exists but cannot be parsed is a failure, not a fresh server:
// the directory scan behind it lists every subdirectory of manifests/ — games
// taken off the shelf included — under raw ids with no title and no exe path.
func TestCorruptRegistryDoesNotFallBackToScanning(t *testing.T) {
	root := withContentRoot(t)
	regDir := filepath.Join(root, "manifests", "_registry")
	if err := os.MkdirAll(regDir, 0o750); err != nil {
		t.Fatal(err)
	}
	writeManifests(t, root, "secret-game")
	if err := os.WriteFile(filepath.Join(root, "manifests", "secret-game", "latest.json"),
		[]byte(`{"version":"0.9.0"}`), 0o600); err != nil {
		t.Fatal(err)
	}

	for _, body := range []string{`{"items":[{"gameId":`, `not json at all`, ``} {
		if err := os.WriteFile(filepath.Join(regDir, "games.json"), []byte(body), 0o600); err != nil {
			t.Fatal(err)
		}
		rec := httptest.NewRecorder()
		testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil))
		var got GamesResponse
		if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
			t.Fatalf("bad json %q: %v", rec.Body.String(), err)
		}
		if len(got.Items) != 0 {
			t.Fatalf("registry %q: a directory scan published %+v", body, got.Items)
		}
	}
}

// An empty registry is a state an operator can reach on purpose (every game
// unpublished or removed); it must stay empty rather than reopen the scan.
func TestEmptyRegistryDoesNotFallBackToScanning(t *testing.T) {
	root := withContentRoot(t)
	regDir := filepath.Join(root, "manifests", "_registry")
	if err := os.MkdirAll(regDir, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(regDir, "games.json"), []byte(`{"items":[]}`), 0o600); err != nil {
		t.Fatal(err)
	}
	writeManifests(t, root, "leftover")

	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil))
	var got GamesResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("bad json %q: %v", rec.Body.String(), err)
	}
	if len(got.Items) != 0 {
		t.Fatalf("an empty registry fell back to a directory scan: %+v", got.Items)
	}
}

// The scan is still the answer for a server that has no registry file yet.
func TestMissingRegistryStillScans(t *testing.T) {
	root := withContentRoot(t)
	writeManifests(t, root, "demo")

	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil))
	var got GamesResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("bad json %q: %v", rec.Body.String(), err)
	}
	if len(got.Items) != 1 || got.Items[0].GameID != "demo" {
		t.Fatalf("items = %+v, want the scanned demo game", got.Items)
	}
}

// gorilla/mux runs r.Use only for a matched route, so misses used to answer
// without a request id, without an access-log line and without a metrics sample:
// a scanner hammering nonexistent paths was invisible on every graph.
func TestMissesGoThroughTheMiddlewareChain(t *testing.T) {
	withContentRoot(t)
	reg := promexp.New()
	r := newRouter(ratelimit.New(0, time.Minute), reg)

	cases := []struct {
		method string
		path   string
		code   string
	}{
		{http.MethodGet, "/api/nope-does-not-exist", "404"},
		{http.MethodPost, "/api/games", "405"},
	}
	for _, c := range cases {
		rec := httptest.NewRecorder()
		r.ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), c.method, c.path, nil))
		if got := strconv.Itoa(rec.Code); got != c.code {
			t.Fatalf("%s %s = %d, want %s", c.method, c.path, rec.Code, c.code)
		}
		if rec.Header().Get("X-Request-Id") == "" {
			t.Errorf("%s %s: no X-Request-Id, the chain did not run", c.method, c.path)
		}
		if rec.Header().Get("Access-Control-Allow-Origin") == "" {
			t.Errorf("%s %s: no CORS header, the chain did not run", c.method, c.path)
		}
	}

	var metrics strings.Builder
	if err := reg.Write(&metrics); err != nil {
		t.Fatal(err)
	}
	for _, c := range cases {
		want := `route="other"`
		if !strings.Contains(metrics.String(), want) || !strings.Contains(metrics.String(), `code="`+c.code+`"`) {
			t.Fatalf("%s %s was not counted:\n%s", c.method, c.path, metrics.String())
		}
	}
}

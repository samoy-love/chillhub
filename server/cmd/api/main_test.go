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

	"ChillHub/server/internal/promexp"
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
	return newRouter(ratelimit.New(0, time.Minute), promexp.New())
}

func TestGameIDIsValidatedOnPublicRoutes(t *testing.T) {
	root := withContentRoot(t)
	// The internal registry directory really exists in production.
	if err := os.MkdirAll(filepath.Join(root, "manifests", "_registry"), 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "manifests", "_registry", "games.json"), []byte(`{"items":[]}`), 0o600); err != nil {
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
		r.ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, p, nil))
		if rec.Code != http.StatusNotFound {
			t.Errorf("GET %s = %d, want 404", p, rec.Code)
		}
	}
}

func TestNewsIndexNeverServesRawFile(t *testing.T) {
	root := withContentRoot(t)
	if err := os.MkdirAll(filepath.Join(root, "news"), 0o750); err != nil {
		t.Fatal(err)
	}
	// An index whose "items" key is absent used to fall through to the raw
	// bytes, leaking whatever the file contained.
	raw := `{"drafts":[{"slug":"secret","published":false}]}`
	if err := os.WriteFile(filepath.Join(root, "news", "index.json"), []byte(raw), 0o600); err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/news/index.json", nil))
	if strings.Contains(rec.Body.String(), "secret") {
		t.Fatalf("raw index leaked to the client: %s", rec.Body.String())
	}

	// Malformed JSON must not be echoed either.
	if err := os.WriteFile(filepath.Join(root, "news", "index.json"), []byte(`{"items":[`), 0o600); err != nil {
		t.Fatal(err)
	}
	rec = httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/news/index.json", nil))
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
	if err := os.MkdirAll(filepath.Join(root, "news", "games", "demo"), 0o750); err != nil {
		t.Fatal(err)
	}
	body := `{"items":[{"slug":"a","published":true},{"slug":"b","published":false},{"slug":"c"}]}`
	if err := os.WriteFile(filepath.Join(root, "news", "games", "demo", "index.json"), []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/news/games/demo/index.json", nil))
	if strings.Contains(rec.Body.String(), `"b"`) {
		t.Fatalf("unpublished item served: %s", rec.Body.String())
	}
	if !strings.Contains(rec.Body.String(), `"a"`) || !strings.Contains(rec.Body.String(), `"c"`) {
		t.Fatalf("published items missing: %s", rec.Body.String())
	}
}

func TestBuildsAreSortedSemanticallyNewestFirst(t *testing.T) {
	root := withContentRoot(t)
	dir := filepath.Join(root, "manifests", "demo")
	if err := os.MkdirAll(dir, 0o750); err != nil {
		t.Fatal(err)
	}
	for _, v := range []string{"1.0.2", "1.1.3", "1.1.7", "1.1.8", "1.1.9", "1.1.10"} {
		if err := os.WriteFile(filepath.Join(dir, v+".json"), []byte(`{}`), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	if err := os.WriteFile(filepath.Join(dir, "latest.json"), []byte(`{"version":"1.1.10"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games/demo/builds", nil))
	var got struct {
		Items []string `json:"items"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	want := []string{"1.1.10", "1.1.9", "1.1.8", "1.1.7", "1.1.3", "1.0.2"}
	if len(got.Items) != len(want) {
		t.Fatalf("items = %v, want %v", got.Items, want)
	}
	for i := range want {
		if got.Items[i] != want[i] {
			t.Fatalf("items = %v, want %v", got.Items, want)
		}
	}
}

func TestBuildsHidesVersionsNewerThanLatest(t *testing.T) {
	root := withContentRoot(t)
	dir := filepath.Join(root, "manifests", "demo")
	if err := os.MkdirAll(dir, 0o750); err != nil {
		t.Fatal(err)
	}
	for _, v := range []string{"1.0.1", "1.0.2", "1.0.3", "1.0.4"} {
		if err := os.WriteFile(filepath.Join(dir, v+".json"), []byte(`{}`), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	// The operator published 1.0.1; 1.0.2-1.0.4 are staged or rolled back.
	if err := os.WriteFile(filepath.Join(dir, "latest.json"), []byte(`{"version":"1.0.1"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games/demo/builds", nil))
	var got struct {
		Items []string `json:"items"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	want := []string{"1.0.1"}
	if len(got.Items) != len(want) || got.Items[0] != want[0] {
		t.Fatalf("items = %v, want %v", got.Items, want)
	}

	// Without latest.json there is nothing to filter against: serve everything.
	if err := os.Remove(filepath.Join(dir, "latest.json")); err != nil {
		t.Fatal(err)
	}
	rec = httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games/demo/builds", nil))
	got.Items = nil
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	if len(got.Items) != 4 {
		t.Fatalf("items = %v, want all four versions", got.Items)
	}
}

func TestHeadIsAllowedWhereverGetIs(t *testing.T) {
	root := withContentRoot(t)
	if err := os.MkdirAll(filepath.Join(root, "manifests", "demo"), 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "manifests", "demo", "latest.json"), []byte(`{"version":"1.0.0"}`), 0o600); err != nil {
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
		r.ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodHead, p, nil))
		if rec.Code == http.StatusMethodNotAllowed {
			t.Errorf("HEAD %s = 405, want it to be served like GET", p)
		}
	}
}

// The stored registry is written by the admin panel, which takes the game ids
// verbatim, and every public handler joins one onto the manifests directory.
// An entry whose id is not a plausible slug must be dropped rather than turned
// into a path.
func TestRegistryEntriesWithUnsafeIDsAreDropped(t *testing.T) {
	root := withContentRoot(t)
	regDir := filepath.Join(root, "manifests", "_registry")
	if err := os.MkdirAll(regDir, 0o750); err != nil {
		t.Fatal(err)
	}
	body := `{"items":[
		{"gameId":"demo","title":"Demo","exeRelativePath":"demo.exe"},
		{"gameId":"../../etc","title":"traversal"},
		{"gameId":"bad id","title":"space"}
	]}`
	if err := os.WriteFile(filepath.Join(regDir, "games.json"), []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	demoDir := filepath.Join(root, "manifests", "demo")
	if err := os.MkdirAll(demoDir, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(demoDir, "latest.json"), []byte(`{"version":"2.0.0"}`), 0o600); err != nil {
		t.Fatal(err)
	}

	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil))
	var got GamesResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("bad json %q: %v", rec.Body.String(), err)
	}
	if len(got.Items) != 1 || got.Items[0].GameID != "demo" {
		t.Fatalf("registry entries with unusable ids reached the response: %+v", got.Items)
	}
	// The surviving entry is still resolved fully, so the filter did not cost
	// the registry its purpose.
	if !got.Items[0].HasLatest || got.Items[0].LatestVersion != "2.0.0" {
		t.Fatalf("registry entry lost its latest build: %+v", got.Items[0])
	}
	if got.Items[0].ExeRelativePath != "demo.exe" {
		t.Fatalf("registry fields not carried through: %+v", got.Items[0])
	}
}

// TestGamesResponseHonoursPinnedAndOrder locks in that the public /api/games
// array — the ONLY ordering signal the launcher trusts (it just remembers each
// game's index, see Core/Home/GameCatalog.cs RememberApiOrder) — actually
// reflects the admin panel's Pinned/Order fields, not raw on-disk array order.
// Before this fix the admin panel's own re-sorted view made pinning LOOK like
// it worked while the real launcher kept showing the old, unpinned order.
func TestGamesResponseHonoursPinnedAndOrder(t *testing.T) {
	root := withContentRoot(t)
	regDir := filepath.Join(root, "manifests", "_registry")
	if err := os.MkdirAll(regDir, 0o750); err != nil {
		t.Fatal(err)
	}
	// On-disk order is alphabetical (alpha, beta, gamma); pinned+order should
	// override that: gamma is pinned (goes first), then beta (order:0) before
	// alpha (order:1, tie-broken by GameID only if order were equal).
	body := `{"items":[
		{"gameId":"alpha","title":"Alpha","order":1},
		{"gameId":"beta","title":"Beta","order":0},
		{"gameId":"gamma","title":"Gamma","pinned":true}
	]}`
	if err := os.WriteFile(filepath.Join(regDir, "games.json"), []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	for _, gid := range []string{"alpha", "beta", "gamma"} {
		dir := filepath.Join(root, "manifests", gid)
		if err := os.MkdirAll(dir, 0o750); err != nil {
			t.Fatal(err)
		}
	}

	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil))
	var got GamesResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("bad json %q: %v", rec.Body.String(), err)
	}
	if len(got.Items) != 3 {
		t.Fatalf("expected 3 games, got %+v", got.Items)
	}
	want := []string{"gamma", "beta", "alpha"}
	for i, gid := range want {
		if got.Items[i].GameID != gid {
			t.Fatalf("games response order = %v, want pinned-first/order-then: %v", gameIDs(got.Items), want)
		}
	}
}

func gameIDs(items []GameInfo) []string {
	out := make([]string, len(items))
	for i, it := range items {
		out[i] = it.GameID
	}
	return out
}

// writeJSON must never emit 200 plus a truncated body: a value that cannot be
// encoded is a server error, and the client has to be able to tell.
func TestWriteJSONReportsEncodingFailure(t *testing.T) {
	w := httptest.NewRecorder()
	writeJSON(w, map[string]any{"bad": make(chan int)})
	if w.Code != http.StatusInternalServerError {
		t.Fatalf("code = %d, want 500 (body %q)", w.Code, w.Body.String())
	}
}

// A registry whose every entry is unusable must produce an EMPTY list, not a
// directory scan.
//
// The scan exists for "there is no registry yet" — a fresh server. Reusing it
// for "the registry is corrupt" would look like a repair and is not one: a
// scanned entry carries no title and no exe path, so the launcher would show
// games under raw ids whose Play button does nothing. A silent half-working
// list is harder to diagnose than an empty one next to the log lines naming
// every rejected entry.
func TestFullyUnusableRegistryDoesNotFallBackToScanning(t *testing.T) {
	root := withContentRoot(t)
	regDir := filepath.Join(root, "manifests", "_registry")
	if err := os.MkdirAll(regDir, 0o750); err != nil {
		t.Fatal(err)
	}
	body := `{"items":[{"gameId":"../../etc"},{"gameId":"bad id"}]}`
	if err := os.WriteFile(filepath.Join(regDir, "games.json"), []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	// A real game directory exists: a scan would happily list it, which is
	// exactly what must NOT happen while a registry file is present.
	if err := os.MkdirAll(filepath.Join(root, "manifests", "demo"), 0o750); err != nil {
		t.Fatal(err)
	}

	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil))
	var got GamesResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatalf("bad json %q: %v", rec.Body.String(), err)
	}
	if len(got.Items) != 0 {
		t.Fatalf("a corrupt registry fell back to a directory scan: %+v", got.Items)
	}
}

// A game unpublished in the admin panel must disappear from the list the
// launcher reads, while its registry row and files stay untouched.
func TestUnpublishedGamesAreHiddenFromTheLauncher(t *testing.T) {
	root := withContentRoot(t)
	regDir := filepath.Join(root, "manifests", "_registry")
	if err := os.MkdirAll(regDir, 0o750); err != nil {
		t.Fatal(err)
	}
	reg := `{"items":[
		{"gameId":"raft","title":"Рафт","unpublished":true},
		{"gameId":"keeper","title":"Keeper"}
	]}`
	if err := os.WriteFile(filepath.Join(regDir, "games.json"), []byte(reg), 0o600); err != nil {
		t.Fatal(err)
	}

	got, ok := loadGamesFromRegistry()
	if !ok {
		t.Fatal("registry was not loaded")
	}
	for _, g := range got {
		if g.GameID == "raft" {
			t.Fatal("unpublished game reached the launcher list")
		}
	}
	if len(got) != 1 || got[0].GameID != "keeper" {
		t.Fatalf("list = %+v, want only keeper", got)
	}
}

// An entry written before the flag existed carries no "unpublished" key at all.
// Its zero value has to mean "visible", or every game on an older server would
// vanish the moment the server was updated.
func TestRegistryWithoutTheFlagStaysPublished(t *testing.T) {
	root := withContentRoot(t)
	regDir := filepath.Join(root, "manifests", "_registry")
	if err := os.MkdirAll(regDir, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(regDir, "games.json"),
		[]byte(`{"items":[{"gameId":"raft","title":"Рафт"}]}`), 0o600); err != nil {
		t.Fatal(err)
	}

	got, ok := loadGamesFromRegistry()
	if !ok || len(got) != 1 || got[0].GameID != "raft" {
		t.Fatalf("legacy registry entry was dropped: ok=%v list=%+v", ok, got)
	}
}

// withVersionFile points the package-level versionFile at a temp path.
func withVersionFile(t *testing.T) string {
	t.Helper()
	old := versionFile
	path := filepath.Join(t.TempDir(), "version.json")
	versionFile = path
	t.Cleanup(func() { versionFile = old })
	return path
}

// The deploy gate reads /api/version.json to learn what is really live and to
// verify the release it just shipped, so the file has to come back verbatim.
func TestVersionIsServedVerbatim(t *testing.T) {
	path := withVersionFile(t)
	body := `{"version":"release-20260815-abc1234","commit":"abc1234","changelog":"x"}`
	if err := os.WriteFile(path, []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	rr := httptest.NewRecorder()
	testRouter().ServeHTTP(rr, httptest.NewRequest(http.MethodGet, "/api/version.json", nil))
	if rr.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rr.Code)
	}
	if got := rr.Body.String(); got != body {
		t.Fatalf("body = %q, want %q", got, body)
	}
	if ct := rr.Header().Get("Content-Type"); ct != "application/json" {
		t.Fatalf("Content-Type = %q", ct)
	}
	if cc := rr.Header().Get("Cache-Control"); cc != "no-store" {
		t.Fatalf("Cache-Control = %q, want no-store", cc)
	}
}

// Before the first deploy that writes the file there is nothing to report;
// that is a 404, not a server error, so the gate can tell "not yet" from "broken".
func TestVersionMissingIs404AndCorruptIs500(t *testing.T) {
	path := withVersionFile(t)
	rr := httptest.NewRecorder()
	testRouter().ServeHTTP(rr, httptest.NewRequest(http.MethodGet, "/api/version.json", nil))
	if rr.Code != http.StatusNotFound {
		t.Fatalf("missing: status = %d, want 404", rr.Code)
	}
	if err := os.WriteFile(path, []byte("{not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	rr = httptest.NewRecorder()
	testRouter().ServeHTTP(rr, httptest.NewRequest(http.MethodGet, "/api/version.json", nil))
	if rr.Code != http.StatusInternalServerError {
		t.Fatalf("corrupt: status = %d, want 500", rr.Code)
	}
}

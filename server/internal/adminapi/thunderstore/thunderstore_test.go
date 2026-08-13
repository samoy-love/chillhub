package thunderstore

import (
	"archive/zip"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"testing"
)

// fakeClient is the test double for the network. It never touches a socket:
// ListCommunityPackages/PackageDetail/DownloadZip all read from an in-memory
// map keyed by "namespace-name-version" (or "namespace-name" for detail,
// which only needs the latest).
type fakeClient struct {
	// detail maps "namespace-name" -> latest version + its dependencies.
	detail map[string]*tsPackageDetail
	// zips maps "namespace-name-version" -> zip bytes to serve.
	zips map[string][]byte
	// downloadCount counts DownloadZip calls per key, to assert dedup.
	downloadCount map[string]int
	listErr       error
	list          []tsListItem
}

func newFakeClient() *fakeClient {
	return &fakeClient{
		detail:        map[string]*tsPackageDetail{},
		zips:          map[string][]byte{},
		downloadCount: map[string]int{},
	}
}

func (f *fakeClient) ListCommunityPackages(_ context.Context, _ string) ([]tsListItem, error) {
	if f.listErr != nil {
		return nil, f.listErr
	}
	return f.list, nil
}

func (f *fakeClient) PackageDetail(_ context.Context, namespace, name string) (*tsPackageDetail, error) {
	d, ok := f.detail[namespace+"-"+name]
	if !ok {
		return nil, ErrNotFound
	}
	return d, nil
}

func (f *fakeClient) DownloadZip(_ context.Context, namespace, name, version string) ([]byte, error) {
	key := namespace + "-" + name + "-" + version
	f.downloadCount[key]++
	z, ok := f.zips[key]
	if !ok {
		return nil, ErrNotFound
	}
	return z, nil
}

// buildZip makes a minimal package zip: manifest.json declaring deps, plus
// optional extra files (path -> content) typically under BepInEx/plugins or
// BepInEx/config.
func buildZip(t *testing.T, deps []string, extraFiles map[string]string) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	manifest := tsManifest{Name: "pkg", VersionNumber: "1.0.0", Dependencies: deps}
	mb, err := json.Marshal(manifest)
	if err != nil {
		t.Fatalf("marshal manifest: %v", err)
	}
	w, err := zw.Create("manifest.json")
	if err != nil {
		t.Fatalf("create manifest.json: %v", err)
	}
	if _, err := w.Write(mb); err != nil {
		t.Fatalf("write manifest.json: %v", err)
	}
	// Every fixture also carries the standard service files, so cleanup can be
	// exercised without every test needing to opt in explicitly.
	for _, svc := range []string{"README.md", "CHANGELOG.md", "icon.png", "LICENSE"} {
		w, err := zw.Create(svc)
		if err != nil {
			t.Fatalf("create %s: %v", svc, err)
		}
		if _, err := w.Write([]byte("x")); err != nil {
			t.Fatalf("write %s: %v", svc, err)
		}
	}
	for path, content := range extraFiles {
		w, err := zw.Create(path)
		if err != nil {
			t.Fatalf("create %s: %v", path, err)
		}
		if _, err := w.Write([]byte(content)); err != nil {
			t.Fatalf("write %s: %v", path, err)
		}
	}
	if err := zw.Close(); err != nil {
		t.Fatalf("close zip writer: %v", err)
	}
	return buf.Bytes()
}

func newTestHandlers(t *testing.T, fc *fakeClient) (*Handlers, string) {
	t.Helper()
	root := t.TempDir()
	return newForTest(root, fc), root
}

// TestDownloadModpack_DedupDiamond builds A -> {B, C}, B -> D, C -> D and
// checks D is downloaded exactly once, and all four packages end up in the
// stored graph.
func TestDownloadModpack_DedupDiamond(t *testing.T) {
	fc := newFakeClient()
	fc.detail["owner-a"] = &tsPackageDetail{}
	fc.detail["owner-a"].Latest.VersionNumber = "1.0.0"
	fc.detail["owner-a"].Latest.Dependencies = []string{"owner-b-1.0.0", "owner-c-1.0.0"}

	fc.zips["owner-a-1.0.0"] = buildZip(t, nil, map[string]string{"BepInEx/plugins/a.dll": "a"})
	fc.zips["owner-b-1.0.0"] = buildZip(t, []string{"owner-d-1.0.0"}, map[string]string{"BepInEx/plugins/b.dll": "b"})
	fc.zips["owner-c-1.0.0"] = buildZip(t, []string{"owner-d-1.0.0"}, map[string]string{"BepInEx/plugins/c.dll": "c"})
	fc.zips["owner-d-1.0.0"] = buildZip(t, nil, map[string]string{"BepInEx/plugins/d.dll": "d"})

	h, root := newTestHandlers(t, fc)
	var progress []string
	err := h.DownloadModpack(context.Background(), "mygame", "owner", "a", "1.0.0", func(m string) {
		progress = append(progress, m)
	})
	if err != nil {
		t.Fatalf("DownloadModpack: %v", err)
	}
	if len(progress) == 0 {
		t.Fatal("expected step-by-step progress messages, got none")
	}
	if got := fc.downloadCount["owner-d-1.0.0"]; got != 1 {
		t.Fatalf("D should be downloaded exactly once, got %d", got)
	}

	metaPath := filepath.Join(root, "content", "mygame", "modpacks", "owner-a", "meta.json")
	b, err := os.ReadFile(metaPath)
	if err != nil {
		t.Fatalf("read meta.json: %v", err)
	}
	var meta ModpackMeta
	if err := json.Unmarshal(b, &meta); err != nil {
		t.Fatalf("unmarshal meta.json: %v", err)
	}
	if len(meta.Graph) != 4 {
		t.Fatalf("expected 4 graph nodes (a,b,c,d), got %d: %+v", len(meta.Graph), meta.Graph)
	}
	for _, name := range []string{"a.dll", "b.dll", "c.dll", "d.dll"} {
		p := filepath.Join(root, "content", "mygame", "modpacks", "owner-a", "BepInEx", "plugins", name)
		if _, err := os.Stat(p); err != nil {
			t.Errorf("expected merged file %s: %v", p, err)
		}
	}
}

// TestDownloadModpack_CycleTerminates builds A -> B -> A and checks the
// resolve completes instead of recursing forever or overflowing the stack.
func TestDownloadModpack_CycleTerminates(t *testing.T) {
	fc := newFakeClient()
	fc.detail["owner-a"] = &tsPackageDetail{}
	fc.detail["owner-a"].Latest.Dependencies = []string{"owner-b-1.0.0"}
	fc.zips["owner-a-1.0.0"] = buildZip(t, nil, nil)
	fc.zips["owner-b-1.0.0"] = buildZip(t, []string{"owner-a-1.0.0"}, nil)

	h, _ := newTestHandlers(t, fc)
	done := make(chan error, 1)
	go func() {
		done <- h.DownloadModpack(context.Background(), "mygame", "owner", "a", "1.0.0", nil)
	}()
	if err := <-done; err != nil {
		t.Fatalf("DownloadModpack should terminate without error on a cycle, got: %v", err)
	}
	if got := fc.downloadCount["owner-a-1.0.0"]; got != 1 {
		t.Fatalf("A should be downloaded exactly once despite the cycle, got %d", got)
	}
}

// TestDownloadModpack_GraphTooLarge exercises the node-count cap.
func TestDownloadModpack_GraphTooLarge(t *testing.T) {
	fc := newFakeClient()
	fc.detail["owner-a"] = &tsPackageDetail{}
	var deps []string
	for i := range MaxGraphNodes + 5 {
		v := "owner-dep" + itoa(i) + "-1.0.0"
		deps = append(deps, v)
		fc.zips["owner-dep"+itoa(i)+"-1.0.0"] = buildZip(t, nil, nil)
	}
	fc.detail["owner-a"].Latest.Dependencies = deps
	fc.zips["owner-a-1.0.0"] = buildZip(t, nil, nil)

	h, _ := newTestHandlers(t, fc)
	err := h.DownloadModpack(context.Background(), "mygame", "owner", "a", "1.0.0", nil)
	if !errors.Is(err, ErrTooManyNodes) {
		t.Fatalf("expected ErrTooManyNodes, got %v", err)
	}
}

// evilZip builds a zip whose single entry tries to escape the extraction
// directory, the same crafted payload TestExtractZip_ZipSlip uses directly —
// here it's fed through DownloadModpack as a DEPENDENCY node instead, to
// check the whole resolve's temp-directory cleanup, not just extractZip.
func evilZip(t *testing.T) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	w, err := zw.Create("../../evil.txt")
	if err != nil {
		t.Fatalf("create evil entry: %v", err)
	}
	if _, err := w.Write([]byte("pwned")); err != nil {
		t.Fatalf("write evil entry: %v", err)
	}
	if err := zw.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}
	return buf.Bytes()
}

// TestDownloadModpack_CleansUpTempDirsOnPartialFailure checks that a node
// which fails AFTER its temp directory is created (here: the root succeeds,
// its dependency fails extraction with a zip-slip entry) doesn't leak that
// directory — a node only reaches `resolver.nodes` once fully resolved, so
// cleanup has to track every MkdirTemp call, not just the successful ones.
func TestDownloadModpack_CleansUpTempDirsOnPartialFailure(t *testing.T) {
	fc := newFakeClient()
	fc.detail["owner-a"] = &tsPackageDetail{}
	fc.detail["owner-a"].Latest.VersionNumber = "1.0.0"
	fc.detail["owner-a"].Latest.Dependencies = []string{"owner-b-1.0.0"}
	fc.zips["owner-a-1.0.0"] = buildZip(t, nil, map[string]string{"BepInEx/plugins/a.dll": "a"})
	fc.zips["owner-b-1.0.0"] = evilZip(t)

	before, err := filepath.Glob(filepath.Join(os.TempDir(), "chillhub-modpack-*"))
	if err != nil {
		t.Fatalf("glob before: %v", err)
	}

	h, _ := newTestHandlers(t, fc)
	err = h.DownloadModpack(context.Background(), "mygame", "owner", "a", "1.0.0", nil)
	if err == nil {
		t.Fatal("expected the zip-slip dependency to fail DownloadModpack")
	}
	if !errors.Is(err, ErrZipSlip) {
		t.Fatalf("expected ErrZipSlip, got %v", err)
	}

	after, err := filepath.Glob(filepath.Join(os.TempDir(), "chillhub-modpack-*"))
	if err != nil {
		t.Fatalf("glob after: %v", err)
	}
	if len(after) > len(before) {
		t.Fatalf("temp dirs leaked: before=%v after=%v", before, after)
	}
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	digits := []byte{}
	neg := n < 0
	if neg {
		n = -n
	}
	for n > 0 {
		digits = append([]byte{byte('0' + n%10)}, digits...)
		n /= 10
	}
	if neg {
		digits = append([]byte{'-'}, digits...)
	}
	return string(digits)
}

// TestExtractZip_ZipSlip crafts an entry that tries to escape the target
// directory and checks extraction refuses it rather than writing outside dir.
func TestExtractZip_ZipSlip(t *testing.T) {
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	w, err := zw.Create("../../evil.txt")
	if err != nil {
		t.Fatalf("create evil entry: %v", err)
	}
	if _, err := w.Write([]byte("pwned")); err != nil {
		t.Fatalf("write evil entry: %v", err)
	}
	if err := zw.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}

	dir := t.TempDir()
	target := filepath.Join(dir, "extract")
	if err := os.MkdirAll(target, 0o755); err != nil {
		t.Fatalf("mkdir: %v", err)
	}
	err = extractZip(buf.Bytes(), target, newExtractBudget(MaxZipBytes))
	if err == nil {
		t.Fatal("expected zip-slip to be rejected")
	}
	if !errors.Is(err, ErrZipSlip) {
		t.Fatalf("expected ErrZipSlip, got %v", err)
	}
	// The escaping file must not exist anywhere outside target.
	if _, statErr := os.Stat(filepath.Join(dir, "evil.txt")); statErr == nil {
		t.Fatal("zip-slip entry was written outside the extraction directory")
	}
	if _, statErr := os.Stat(filepath.Join(filepath.Dir(dir), "evil.txt")); statErr == nil {
		t.Fatal("zip-slip entry escaped even further than the immediate parent")
	}
}

// TestExtractZip_BudgetIsCumulativeAcrossCalls checks that a shared
// extractBudget instance actually enforces a cap across MULTIPLE extractZip
// calls (as resolver does across every node of one graph), not just within a
// single archive — the bug this guards against: a per-node budget lets many
// small archives each stay under the cap while their sum on disk blows past it.
func TestExtractZip_BudgetIsCumulativeAcrossCalls(t *testing.T) {
	makeZip := func(payload []byte) []byte {
		var buf bytes.Buffer
		zw := zip.NewWriter(&buf)
		w, err := zw.Create("file.bin")
		if err != nil {
			t.Fatalf("create entry: %v", err)
		}
		if _, err := w.Write(payload); err != nil {
			t.Fatalf("write entry: %v", err)
		}
		if err := zw.Close(); err != nil {
			t.Fatalf("close: %v", err)
		}
		return buf.Bytes()
	}

	const budgetLimit = 10
	budget := newExtractBudget(budgetLimit)
	dir := t.TempDir()

	// First call: 6 bytes, well within the shared budget on its own.
	// extractOneEntry creates the target directory itself, no pre-mkdir needed.
	if err := extractZip(makeZip([]byte("abcdef")), filepath.Join(dir, "a"), budget); err != nil {
		t.Fatalf("first extractZip: %v", err)
	}

	// Second call: another 6 bytes into a DIFFERENT directory — 6 is also
	// within budgetLimit on its own, but 6+6=12 exceeds the shared 10-byte cap.
	// A per-call/per-node budget (the bug) would let this succeed; a shared one
	// must reject it.
	err := extractZip(makeZip([]byte("ghijkl")), filepath.Join(dir, "b"), budget)
	if err == nil {
		t.Fatal("expected the second extractZip to fail once the shared budget is exceeded")
	}
}

// TestCleanupServiceFiles_CaseInsensitive checks that differently-cased
// service file names are still recognised and removed, and only within the
// given directory.
func TestCleanupServiceFiles_CaseInsensitive(t *testing.T) {
	dir := t.TempDir()
	names := []string{"Manifest.JSON", "changelog.MD", "ICON.PNG", "Readme.md", "License", "keep.dll"}
	for _, n := range names {
		if err := os.WriteFile(filepath.Join(dir, n), []byte("x"), 0o644); err != nil {
			t.Fatalf("write %s: %v", n, err)
		}
	}
	removed, err := cleanupServiceFiles(dir)
	if err != nil {
		t.Fatalf("cleanupServiceFiles: %v", err)
	}
	if len(removed) != 5 {
		t.Fatalf("expected 5 files removed, got %d: %v", len(removed), removed)
	}
	if _, err := os.Stat(filepath.Join(dir, "keep.dll")); err != nil {
		t.Fatalf("keep.dll should survive cleanup: %v", err)
	}
	for _, n := range names[:5] {
		if _, err := os.Stat(filepath.Join(dir, n)); err == nil {
			t.Errorf("%s should have been removed", n)
		}
	}
}

// TestParseDependency covers the "<namespace>-<name>-<version>" split,
// including a hyphenated mod name.
func TestParseDependency(t *testing.T) {
	ns, name, ver, err := parseDependency("BepInEx-BepInExPack-5.4.21")
	if err != nil {
		t.Fatalf("parseDependency: %v", err)
	}
	if ns != "BepInEx" || name != "BepInExPack" || ver != "5.4.21" {
		t.Fatalf("got %q/%q/%q", ns, name, ver)
	}
	if _, _, _, err := parseDependency("not-a-valid-ref-"); err == nil {
		t.Fatal("expected an error for a malformed reference")
	}
}

// TestSearchPackages_FilterAndCache checks the query filter and that a
// second call within the TTL does not hit the client again.
func TestSearchPackages_FilterAndCache(t *testing.T) {
	fc := newFakeClient()
	fc.list = []tsListItem{
		{Name: "BiggerLobby", Owner: "sfDesat", Versions: []tsListItemVersion{{VersionNumber: "1.0.0", Description: "more players"}}},
		{Name: "MoreCompany", Owner: "notnotnotswipez", Versions: []tsListItemVersion{{VersionNumber: "1.9.5", Description: "cosmetics"}}},
	}
	h, _ := newTestHandlers(t, fc)

	items, err := h.SearchPackages(context.Background(), "lethal-company", "lobby")
	if err != nil {
		t.Fatalf("SearchPackages: %v", err)
	}
	if len(items) != 1 || items[0].Name != "BiggerLobby" {
		t.Fatalf("expected only BiggerLobby, got %+v", items)
	}

	fc.listErr = errors.New("network should not be hit again")
	if _, err := h.SearchPackages(context.Background(), "lethal-company", ""); err != nil {
		t.Fatalf("second SearchPackages call should be served from cache: %v", err)
	}
}

// TestDeleteModpack removes a previously downloaded modpack and reports its
// files.
func TestDeleteModpack(t *testing.T) {
	fc := newFakeClient()
	fc.detail["owner-a"] = &tsPackageDetail{}
	fc.zips["owner-a-1.0.0"] = buildZip(t, nil, map[string]string{"BepInEx/plugins/a.dll": "a"})

	h, root := newTestHandlers(t, fc)
	if err := h.DownloadModpack(context.Background(), "mygame", "owner", "a", "1.0.0", nil); err != nil {
		t.Fatalf("DownloadModpack: %v", err)
	}
	removed, err := h.DeleteModpack("mygame", "owner", "a")
	if err != nil {
		t.Fatalf("DeleteModpack: %v", err)
	}
	if len(removed) != 1 || removed[0] != "plugins/a.dll" {
		t.Fatalf("unexpected removed list: %v", removed)
	}
	if _, err := os.Stat(filepath.Join(root, "content", "mygame", "modpacks", "owner-a")); !os.IsNotExist(err) {
		t.Fatalf("modpack directory should be gone, stat err=%v", err)
	}
}

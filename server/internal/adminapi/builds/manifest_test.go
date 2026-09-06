package builds

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/zeebo/blake3"
)

func decodeManifest(t *testing.T, b []byte) manifest {
	t.Helper()
	var m manifest
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatalf("manifest is not valid JSON (%v): %s", err, string(b))
	}
	return m
}

func manifestPaths(t *testing.T, b []byte) map[string]bool {
	t.Helper()
	out := map[string]bool{}
	for _, f := range decodeManifest(t, b).Files {
		out[f.Path] = true
	}
	return out
}

func manifestEntry(t *testing.T, m manifest, path string) manifestFile {
	t.Helper()
	for _, f := range m.Files {
		if f.Path == path {
			return f
		}
	}
	t.Fatalf("manifest has no entry %q; it has %v", path, manifestPathList(m))
	return manifestFile{}
}

func manifestPathList(m manifest) []string {
	out := make([]string, 0, len(m.Files))
	for _, f := range m.Files {
		out = append(out, f.Path)
	}
	return out
}

// A manifest path is the URL the client appends to the content base and the
// relative path it writes on disk. If composition ever emitted an OS-native
// separator, the client would request ".../a%5Cb.dll" and write a single file
// literally named "a\b.dll" — on every platform the server happens to run on.
// The whole nested tree has to come out as forward slashes.
func TestManifestUsesSlashPathsForNestedDirectories(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	publishInto(t, h, w, "game", "game", "1.0.0", zipBytes(t, map[string]string{
		"runtimes/win-x64/native/blake3_dotnet.dll": "native",
		"data/levels/level1.dat":                    "lvl",
		"top.txt":                                   "t",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("publish failed: %d %s", w.Code, w.Body.String())
	}
	m := decodeManifest(t, w.Body.Bytes())
	for _, f := range m.Files {
		if strings.ContainsRune(f.Path, '\\') {
			t.Errorf("manifest path %q carries a backslash; the client would create one oddly named file instead of a directory tree", f.Path)
		}
	}
	for _, want := range []string{"runtimes/win-x64/native/blake3_dotnet.dll", "data/levels/level1.dat", "top.txt"} {
		if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0", "files", filepath.FromSlash(want))); err != nil {
			t.Errorf("manifest promises %q but it is not on disk: %v", want, err)
		}
		manifestEntry(t, m, want)
	}
}

// The hashes in the manifest are the only thing the client checks a downloaded
// file against. A wrong hash is indistinguishable from a corrupted download: the
// client retries, fails again, and the build can never be installed. Recompute
// them here instead of trusting that "some hex was written".
func TestManifestHashesAndSizesDescribeTheRealBytes(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	payload := "content of the published file\n"
	w := httptest.NewRecorder()
	publishInto(t, h, w, "game", "game", "1.0.0", zipBytes(t, map[string]string{"bin/app.exe": payload}))
	if w.Code != http.StatusOK {
		t.Fatalf("publish failed: %d %s", w.Code, w.Body.String())
	}
	f := manifestEntry(t, decodeManifest(t, w.Body.Bytes()), "bin/app.exe")

	if f.Size != int64(len(payload)) {
		t.Errorf("size %d, want %d", f.Size, len(payload))
	}
	sh := sha256.Sum256([]byte(payload))
	if f.Sha256 != hex.EncodeToString(sh[:]) {
		t.Errorf("sha256 %q does not describe the published bytes", f.Sha256)
	}
	hb := blake3.New()
	if _, err := hb.Write([]byte(payload)); err != nil {
		t.Fatal(err)
	}
	if f.Blake3 != hex.EncodeToString(hb.Sum(nil)) {
		t.Errorf("blake3 %q does not describe the published bytes", f.Blake3)
	}
	if !f.Executable {
		t.Error(".exe not marked executable: the updater would drop the exec bit")
	}
}

// A zero-length file is a real build artifact (empty marker files, empty logs).
// It is also the one case where "no hash" looks plausible — and validateManifest
// refuses to publish a manifest entry without a hash, so a naive implementation
// would make the entire build unpublishable rather than just mishandle one file.
func TestManifestPublishesZeroLengthFiles(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	publishInto(t, h, w, "game", "game", "1.0.0", zipBytes(t, map[string]string{
		"empty.marker": "",
		"other.txt":    "x",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("a build containing an empty file was refused: %d %s", w.Code, w.Body.String())
	}
	f := manifestEntry(t, decodeManifest(t, w.Body.Bytes()), "empty.marker")
	if f.Size != 0 {
		t.Errorf("size %d, want 0", f.Size)
	}
	if f.Blake3 == "" || f.Sha256 == "" {
		t.Fatalf("empty file published without a hash (%+v); the client's integrity check is then off for it", f)
	}
}

// Cyrillic file names are normal in this project's builds. The manifest path is
// what the client turns into a download URL, so a name mangled during
// composition points at a file that does not exist on the server: the install
// fails with a 404 on a file the admin can plainly see in the archive.
func TestManifestPreservesCyrillicNames(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	const rel = "данные/уровень 1.dat"
	w := httptest.NewRecorder()
	publishInto(t, h, w, "game", "game", "1.0.0", zipBytes(t, map[string]string{rel: "данные"}))
	if w.Code != http.StatusOK {
		t.Fatalf("publish failed: %d %s", w.Code, w.Body.String())
	}
	manifestEntry(t, decodeManifest(t, w.Body.Bytes()), rel)
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0", "files", filepath.FromSlash(rel))); err != nil {
		t.Fatalf("the manifest path does not resolve to a file on disk: %v", err)
	}
}

// emptyDirs is the only way a directory with no files survives publication: the
// file list cannot express it. Games have shipped with such directories (mod and
// save folders the game refuses to start without), and getting this wrong broke
// installation of lethal-company and drive-beyond-horizons outright.
//
// The opposite mistake matters just as much: a directory that does hold files —
// at any depth — must NOT be listed, or the client creates a directory where the
// updater is about to place a file.
func TestManifestListsOnlyGenuinelyEmptyDirectories(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	// zipBytes only writes files, so the empty directories are created directly
	// in the staging tree and scanned from there.
	_, filesRoot, err := h.stageVersionDir("game", "1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	mustMkdirAll(t, filepath.Join(filesRoot, "mods"))
	mustMkdirAll(t, filepath.Join(filesRoot, "saves", "slot1"))
	mustMkdirAll(t, filepath.Join(filesRoot, "data", "levels"))
	mustWriteFile(t, filepath.Join(filesRoot, "data", "levels", "l1.dat"), "x")

	files, emptyDirs, err := scanManifest(filesRoot)
	if err != nil {
		t.Fatalf("scanManifest: %v", err)
	}
	if len(files) != 1 {
		t.Fatalf("expected one file, got %v", files)
	}
	got := map[string]bool{}
	for _, d := range emptyDirs {
		got[d] = true
		if !strings.HasSuffix(d, "/") {
			t.Errorf("emptyDir %q has no trailing slash; the client distinguishes directories by it", d)
		}
	}
	for _, want := range []string{"mods/", "saves/", "saves/slot1/"} {
		if !got[want] {
			t.Errorf("empty directory %q missing from the manifest: it will not exist after installation", want)
		}
	}
	for _, unwanted := range []string{"data/", "data/levels/"} {
		if got[unwanted] {
			t.Errorf("%q holds files yet is listed as empty", unwanted)
		}
	}
	if got["./"] || got["/"] {
		t.Errorf("the tree root leaked into emptyDirs: %v", emptyDirs)
	}
}

// The streaming publish path composes the manifest through a different function
// than the plain one. They must agree file for file — the admin UI picks the
// path by upload size, and a build must not depend on how it was uploaded.
func TestStreamComposeAgreesWithScanManifest(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	_, filesRoot, err := h.stageVersionDir("game", "1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	mustMkdirAll(t, filepath.Join(filesRoot, "a", "b"))
	mustMkdirAll(t, filepath.Join(filesRoot, "empty"))
	mustWriteFile(t, filepath.Join(filesRoot, "a", "b", "деталь.dll"), "payload")
	mustWriteFile(t, filepath.Join(filesRoot, "zero.bin"), "")
	mustWriteFile(t, filepath.Join(filesRoot, "app.exe"), "exe")

	want, wantDirs, err := scanManifest(filesRoot)
	if err != nil {
		t.Fatal(err)
	}
	var sink bytes.Buffer
	got, gotDirs, ok := streamCompose(&sink, adminutilNopFlusher{}, filesRoot)
	if !ok {
		t.Fatalf("streamCompose failed: %s", sink.String())
	}
	if !equalManifestFiles(want, got) {
		t.Fatalf("the two publish paths disagree:\n scan   = %+v\n stream = %+v", want, got)
	}
	if strings.Join(wantDirs, ",") != strings.Join(gotDirs, ",") {
		t.Fatalf("emptyDirs differ: scan=%v stream=%v", wantDirs, gotDirs)
	}
	// The progress events are what the admin UI draws; a silent stream looks
	// like a hung publication.
	if !strings.Contains(sink.String(), `"type":"file"`) {
		t.Errorf("streamCompose emitted no file events: %q", sink.String())
	}
}

func equalManifestFiles(a, b []manifestFile) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

type adminutilNopFlusher struct{}

func (adminutilNopFlusher) Flush() {}

func mustMkdirAll(t *testing.T, p string) {
	t.Helper()
	if err := os.MkdirAll(p, 0o755); err != nil {
		t.Fatal(err)
	}
}

func mustWriteFile(t *testing.T, p, body string) {
	t.Helper()
	if err := os.WriteFile(p, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
}

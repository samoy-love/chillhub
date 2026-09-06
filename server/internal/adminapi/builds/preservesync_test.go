package builds

import (
	"archive/zip"
	"bytes"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// The publishing side and the updating side keep two copies of the same list.
// Nothing in the build system links them, so the only thing standing between a
// working release and an unresolvable hash mismatch is that a human edited both
// files. This test is that link.
//
// The failure mode is not a crash: the launcher offers an update, the updater
// refuses to overwrite the preserved file, the hash still differs on the next
// launch, and the user is offered the very same update forever. Versions 1.0.2,
// 1.0.3, 1.1.7 and 1.1.8 shipped with exactly that.
func TestLauncherStateFilesMatchUpdaterPreserveRules(t *testing.T) {
	got := lowerSorted(LauncherStateFiles)
	want := lowerSorted(csharpStringArray(t, "DefaultRules"))
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("server and updater preserve lists have diverged:\n  server (LauncherStateFiles)          = %v\n  updater (PreserveMatcher.DefaultRules) = %v\n"+
			"Whichever side is missing an entry produces a launcher that updates itself forever.", got, want)
	}
}

// The client skips updater artifacts everywhere it skips preserve rules: in the
// integrity check, in the download plan and in the delete list. A manifest that
// lists them promises files the client will never fetch — and the repository's
// own guard (updater/tests/ManifestPreserveCheck) already treats such a manifest
// as a violation, so the publishing side has to enforce the same thing.
func TestLauncherUpdaterArtifactsMatchUpdaterList(t *testing.T) {
	got := lowerSorted(LauncherUpdaterArtifacts)
	want := lowerSorted(csharpStringArray(t, "UpdaterArtifactFiles"))
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("server and updater artifact lists have diverged:\n  server = %v\n  updater = %v", got, want)
	}
	if dir := csharpStringConst(t, "UpdaterArtifactDir"); !strings.EqualFold(dir, LauncherUpdaterArtifactDir) {
		t.Fatalf("updater artifact directory differs: server %q, updater %q", LauncherUpdaterArtifactDir, dir)
	}
}

// Every path the client refuses to write must be absent from the manifest, and
// every path it does write must survive. Checking the two lists are equal is not
// enough: the matching rule (exact top-level path, one directory prefix) is
// where the sides drifted apart before — the client used to also match bare file
// names in subdirectories, so "data/config.json" was published and silently
// never installed.
func TestLauncherNonPayloadMatchesExactTopLevelPathOnly(t *testing.T) {
	dropped := []string{
		"config.json", "launcher.version", "launcher.update-status", "Uninstall.exe",
		"filelist.txt", "apply-update.cmd",
		"updater/ChillHub.Updater.exe", "updater/nested/x.dll",
	}
	kept := []string{
		"ChillHub.exe", "data/config.json", "data/filelist.txt", "data/Uninstall.exe",
		"updater.exe", "updaters/x.dll", "my-updater/x.dll",
	}
	for _, p := range dropped {
		if !isLauncherNonPayload(p) {
			t.Errorf("%q must never reach a launcher manifest: the client skips it, so its hash can never match", p)
		}
	}
	for _, p := range kept {
		if isLauncherNonPayload(p) {
			t.Errorf("%q is ordinary build content: dropping it means the client never receives the file", p)
		}
	}
}

// The end-to-end guarantee: whatever a careless launcher ZIP happens to contain
// — an installer-made Uninstall.exe, a config.json from a launcher installed
// into the build output, leftovers of a previous update run — the published
// manifest must be clean. Every publication path funnels through writeManifest,
// so a build that sneaks such a file past the packaging script is still safe.
func TestPublishingLauncherBuildNeverManifestsClientSkippedFiles(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	publishInto(t, h, w, "launcher", "launcher", "1.1.9", zipBytes(t, map[string]string{
		"ChillHub.exe":                     "payload",
		"config.json":                      "{}",
		"launcher.version":                 "1.1.9",
		"launcher.update-status":           "ok",
		"Uninstall.exe":                    "nsis",
		"filelist.txt":                     "junk",
		"updater/ChillHub.Updater.exe": "junk",
		"data/config.json":                 "legit",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("publish failed: %d %s", w.Code, w.Body.String())
	}
	paths := manifestPaths(t, w.Body.Bytes())
	for _, bad := range []string{
		"config.json", "launcher.version", "launcher.update-status", "Uninstall.exe",
		"filelist.txt", "updater/ChillHub.Updater.exe",
	} {
		if paths[bad] {
			t.Errorf("published launcher manifest lists %q; the client will never write it and will offer the update forever", bad)
		}
	}
	for _, good := range []string{"ChillHub.exe", "data/config.json"} {
		if !paths[good] {
			t.Errorf("published launcher manifest lost %q: the client will never receive the file", good)
		}
	}
}

// The directory rule has to cover the artifact directory ITSELF, not only what
// is inside it: CleanupUpdaterArtifacts deletes <install>/updater recursively, so
// a manifest listing "updater/" as a directory to create describes a state the
// client destroys in the same run.
func TestLauncherNonPayloadDirCoversTheArtifactDirectoryItself(t *testing.T) {
	for _, d := range []string{"updater", "updater/", "updater/backup", "updater/backup/"} {
		if !isLauncherNonPayloadDir(d) {
			t.Errorf("%q must never reach a launcher manifest: the updater removes it in the same run", d)
		}
	}
	for _, d := range []string{"updaters/", "my-updater/", "data/updater/", "logs/", "mods/"} {
		if isLauncherNonPayloadDir(d) {
			t.Errorf("%q is ordinary build content: dropping it means the directory never exists after installation", d)
		}
	}
}

// The end-to-end version of the same guarantee. Only Files used to be filtered,
// so an empty "updater/" swept into the launcher ZIP by a previous update run
// travelled into emptyDirs untouched. Directories carry no hashes, so this does
// not produce the endless update loop a stray file does — it produces a manifest
// that permanently promises a directory the updater deletes on every run, which
// is the same broken promise with the loud part removed.
func TestPublishingLauncherBuildNeverManifestsUpdaterOwnedDirectories(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	publishInto(t, h, w, "launcher", "launcher", "1.1.9", zipWithEmptyDirs(t,
		map[string]string{"ChillHub.exe": "payload"},
		[]string{"updater/", "updater/backup/", "logs/"}))
	if w.Code != http.StatusOK {
		t.Fatalf("publish failed: %d %s", w.Code, w.Body.String())
	}
	got := map[string]bool{}
	for _, d := range decodeManifest(t, w.Body.Bytes()).EmptyDirs {
		got[d] = true
	}
	for _, bad := range []string{"updater/", "updater/backup/"} {
		if got[bad] {
			t.Errorf("published launcher manifest lists directory %q; the updater deletes it in the same run, so the installation never matches the manifest", bad)
		}
	}
	if !got["logs/"] {
		t.Errorf("ordinary empty directory %q was dropped: it will not exist after installation, %v", "logs/", got)
	}
}

// A game build is not the launcher's installation directory: a game may perfectly
// well ship an empty folder called "updater", and dropping it would leave the
// game without a directory it needs to start.
func TestPublishingGameBuildKeepsUpdaterNamedDirectories(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	publishInto(t, h, w, "game", "lethal-company", "1.0.0", zipWithEmptyDirs(t,
		map[string]string{"game.exe": "x"}, []string{"updater/"}))
	if w.Code != http.StatusOK {
		t.Fatalf("publish failed: %d %s", w.Code, w.Body.String())
	}
	found := false
	for _, d := range decodeManifest(t, w.Body.Bytes()).EmptyDirs {
		if d == "updater/" {
			found = true
		}
	}
	if !found {
		t.Error("game manifest lost the empty directory \"updater/\": the launcher preserve rules describe the launcher's own installation and nothing else")
	}
}

// zipWithEmptyDirs builds an archive carrying explicit directory entries next to
// its files — the only way a ZIP can express a directory that holds nothing.
func zipWithEmptyDirs(t *testing.T, files map[string]string, dirs []string) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	for _, d := range dirs {
		if _, err := zw.CreateHeader(&zip.FileHeader{Name: d}); err != nil {
			t.Fatal(err)
		}
	}
	for name, body := range files {
		w, err := zw.Create(name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := io.WriteString(w, body); err != nil {
			t.Fatal(err)
		}
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

// The same names in a game build are ordinary content. A game shipping its own
// config.json must get it installed — the preserve rules describe the launcher's
// installation directory and nothing else.
func TestPublishingGameBuildKeepsThoseNames(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	publishInto(t, h, w, "game", "lethal-company", "1.0.0", zipBytes(t, map[string]string{
		"config.json":  "{}",
		"filelist.txt": "data",
		"game.exe":     "x",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("publish failed: %d %s", w.Code, w.Body.String())
	}
	paths := manifestPaths(t, w.Body.Bytes())
	for _, p := range []string{"config.json", "filelist.txt", "game.exe"} {
		if !paths[p] {
			t.Errorf("game manifest lost %q", p)
		}
	}
}

// ===== helpers =====

func lowerSorted(in []string) []string {
	out := make([]string, 0, len(in))
	for _, s := range in {
		out = append(out, strings.ToLower(strings.TrimSpace(s)))
	}
	sort.Strings(out)
	return out
}

// updatePreserveSource returns the text of updater/UpdatePreserve.cs, which is
// the single source of truth both sides copy from.
func updatePreserveSource(t *testing.T) string {
	t.Helper()
	dir, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for range 8 {
		p := filepath.Join(dir, "updater", "UpdatePreserve.cs")
		if b, err := os.ReadFile(p); err == nil {
			return string(b)
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	// Deliberately not a silent pass: the whole point of this file is to compare
	// against the updater, and "the updater was not there" must not read as "the
	// lists agree".
	t.Skip("updater/UpdatePreserve.cs not found above the package directory; the cross-language check cannot run here")
	return ""
}

var csItemRe = regexp.MustCompile(`"([^"]*)"`)

// csharpStringArray extracts the elements of a `static readonly string[] name = { ... }`
// declaration. It is a text scan on purpose: a hand-maintained copy of the list
// in Go would be one more thing that can drift.
func csharpStringArray(t *testing.T, name string) []string {
	t.Helper()
	src := updatePreserveSource(t)
	re := regexp.MustCompile(`(?s)\b` + regexp.QuoteMeta(name) + `\s*=\s*\{(.*?)\}`)
	m := re.FindStringSubmatch(src)
	if m == nil {
		t.Fatalf("cannot find %s in updater/UpdatePreserve.cs; the cross-language guard is broken, fix it rather than deleting it", name)
	}
	var out []string
	for _, item := range csItemRe.FindAllStringSubmatch(m[1], -1) {
		if s := strings.TrimSpace(item[1]); s != "" {
			out = append(out, s)
		}
	}
	if len(out) == 0 {
		t.Fatalf("%s parsed as empty; the guard would then pass against an empty server list too", name)
	}
	return out
}

func csharpStringConst(t *testing.T, name string) string {
	t.Helper()
	src := updatePreserveSource(t)
	re := regexp.MustCompile(`\b` + regexp.QuoteMeta(name) + `\s*=\s*"([^"]*)"`)
	m := re.FindStringSubmatch(src)
	if m == nil {
		t.Fatalf("cannot find const %s in updater/UpdatePreserve.cs", name)
	}
	return m[1]
}

package builds

import (
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// The same preserve list exists in FOUR places, and until now only two of them
// were tied together by a test (server <-> updater, see preservesync_test.go).
// The other two live on the installer side:
//
//	scripts/installer.nsi        — the /x exclusions of the File command
//	scripts/build-installer.ps1  — $script:PayloadExclude* used by New-LauncherPayload
//
// Both files carry a comment saying "keep in sync with ..." and nothing else.
// That is exactly the arrangement that already failed: 1.0.2, 1.0.3, 1.1.7 and
// 1.1.8 shipped with a preserve file inside the manifest, and every user who
// installed them was offered the same update forever.
//
// The failure is asymmetric, which is why both directions are checked:
//
//   - a file missing from the packaging script leaks into the ZIP, into the
//     manifest, and produces the endless update loop;
//   - a file missing from the installer leaks into the installation directory,
//     from where the next careless ZIP picks it up again.
//
// These tests are text scans of the two scripts on purpose. Reimplementing the
// lists in Go would create a fifth copy — the very thing being guarded against.

// TestInstallerExclusionsMatchPackagingScript pins the installer's /x list to
// the packaging script's exclusion arrays. The two run at different times (one
// at install, one at release packaging) over the same build output, and a file
// dropped from one but not the other silently changes what a release contains.
func TestInstallerExclusionsMatchPackagingScript(t *testing.T) {
	nsis := lowerSorted(nsisFileExclusions(t))

	var script []string
	script = append(script, powershellStringArray(t, "PayloadExcludeFiles")...)
	script = append(script, powershellStringArray(t, "PayloadExcludeGlobs")...)
	script = append(script, powershellStringArray(t, "PayloadExcludeDirGlobs")...)
	script = lowerSorted(script)

	if strings.Join(nsis, ",") != strings.Join(script, ",") {
		t.Fatalf("installer and packaging script exclusions have diverged:\n"+
			"  scripts/installer.nsi (File /x)          = %v\n"+
			"  scripts/build-installer.ps1 ($PayloadExclude*) = %v\n"+
			"Whichever side is missing an entry ships it: into the installation directory, or into the published manifest.",
			nsis, script)
	}
}

// TestPackagingScriptPreserveFilesMatchServer closes the loop to the two lists
// that already guard each other. The packaging script's file exclusions are the
// preserve list and nothing else: globs (*.pdb) and directory globs (linux-*)
// are a separate concern — they drop build junk, not state the client owns.
func TestPackagingScriptPreserveFilesMatchServer(t *testing.T) {
	got := lowerSorted(powershellStringArray(t, "PayloadExcludeFiles"))
	want := lowerSorted(LauncherStateFiles)
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("packaging script and server preserve lists have diverged:\n"+
			"  scripts/build-installer.ps1 ($PayloadExcludeFiles) = %v\n"+
			"  server (LauncherStateFiles)                        = %v\n"+
			"A file the packaging script keeps is a file the manifest lists and the updater refuses to write: the launcher then updates itself forever.",
			got, want)
	}
}

// TestInstallerCleanupKeepsExactlyThePreserveFiles guards the third copy of the
// list inside installer.nsi itself. Installing over an existing installation now
// wipes the directory first (File /r never deletes, so files dropped between
// versions used to pile up forever), and the wipe skips the preserve files by
// name.
//
// Miss one and the wipe eats state the updater is contractually forbidden to
// rewrite: launcher.version disappears and the launcher no longer knows what it
// is running. Keep one too many and a stale build file survives every upgrade —
// exactly the problem the wipe was added to solve.
func TestInstallerCleanupKeepsExactlyThePreserveFiles(t *testing.T) {
	got := lowerSorted(nsisCleanupKeptFiles(t))
	want := lowerSorted(LauncherStateFiles)
	if strings.Join(got, ",") != strings.Join(want, ",") {
		t.Fatalf("cleanup skip-list in installer.nsi and the preserve list have diverged:\n"+
			"  scripts/installer.nsi (CleanPreviousInstall) = %v\n"+
			"  server (LauncherStateFiles)                  = %v\n"+
			"Missing entries are deleted on every upgrade; extra ones survive it forever.",
			got, want)
	}
}

var (
	nsisCleanupFnRe = regexp.MustCompile(`(?s)Function CleanPreviousInstall(.*?)FunctionEnd`)
	nsisStrCmpRe    = regexp.MustCompile(`StrCmp\s+\$1\s+"([^"]+)"`)
)

// nsisCleanupKeptFiles extracts the file names CleanPreviousInstall refuses to
// delete, minus the two directory entries every FindFirst loop has to skip.
func nsisCleanupKeptFiles(t *testing.T) []string {
	t.Helper()
	src := repoFile(t, "scripts", "installer.nsi")
	m := nsisCleanupFnRe.FindStringSubmatch(src)
	if m == nil {
		t.Fatal("cannot find Function CleanPreviousInstall in scripts/installer.nsi; the guard is broken, fix it rather than deleting it")
	}
	var out []string
	for _, item := range nsisStrCmpRe.FindAllStringSubmatch(m[1], -1) {
		switch item[1] {
		case ".", "..":
			continue
		}
		out = append(out, item[1])
	}
	if len(out) == 0 {
		t.Fatal("parsed zero preserved names from CleanPreviousInstall; the guard would then pass against an empty list too")
	}
	return out
}

// repoFile reads a file addressed from the repository root. The package sits a
// few directories down, and the test binary's working directory is the package
// directory.
func repoFile(t *testing.T, rel ...string) string {
	t.Helper()
	dir, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for range 8 {
		if b, err := os.ReadFile(filepath.Join(append([]string{dir}, rel...)...)); err == nil {
			return string(b)
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	// Deliberately not a silent pass: "the file was not there" must never read
	// as "the lists agree".
	t.Skipf("%s not found above the package directory; the cross-language guard cannot run here", filepath.Join(rel...))
	return ""
}

var (
	nsisFileLineRe = regexp.MustCompile(`(?m)^\s*File\s+/r\s.*\$\{PAYLOAD_DIR\}`)
	nsisExcludeRe  = regexp.MustCompile(`/x\s+"([^"]+)"`)
	psItemRe       = regexp.MustCompile(`'([^']*)'`)
)

// nsisFileExclusions extracts the /x arguments of the single File command that
// unpacks the build output.
func nsisFileExclusions(t *testing.T) []string {
	t.Helper()
	src := repoFile(t, "scripts", "installer.nsi")
	line := nsisFileLineRe.FindString(src)
	if line == "" {
		t.Fatal("cannot find the `File /r ... ${PAYLOAD_DIR}` command in scripts/installer.nsi; the guard is broken, fix it rather than deleting it")
	}
	var out []string
	for _, m := range nsisExcludeRe.FindAllStringSubmatch(line, -1) {
		if s := strings.TrimSpace(m[1]); s != "" {
			out = append(out, s)
		}
	}
	if len(out) == 0 {
		t.Fatal("parsed zero /x exclusions from scripts/installer.nsi; the guard would then pass against an empty list too")
	}
	return out
}

// powershellStringArray extracts the elements of a `$script:<name> = @('a', 'b')`
// declaration from scripts/build-installer.ps1.
func powershellStringArray(t *testing.T, name string) []string {
	t.Helper()
	src := repoFile(t, "scripts", "build-installer.ps1")
	re := regexp.MustCompile(`(?s)\$script:` + regexp.QuoteMeta(name) + `\s*=\s*@\((.*?)\)`)
	m := re.FindStringSubmatch(src)
	if m == nil {
		t.Fatalf("cannot find $script:%s in scripts/build-installer.ps1; the guard is broken, fix it rather than deleting it", name)
	}
	var out []string
	for _, item := range psItemRe.FindAllStringSubmatch(m[1], -1) {
		if s := strings.TrimSpace(item[1]); s != "" {
			out = append(out, s)
		}
	}
	if len(out) == 0 {
		t.Fatalf("$script:%s parsed as empty; the guard would then pass against an empty list too", name)
	}
	return out
}

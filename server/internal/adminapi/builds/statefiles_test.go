package builds

import "testing"

// A launcher manifest must never carry user-state files. When it does, the
// launcher checks their hashes, the updater refuses to overwrite them
// (--preserve), and the resulting mismatch makes the launcher offer the same
// update forever. Versions 1.0.2, 1.0.3 and 1.1.7 shipped exactly that.
func TestStripLauncherStateFilesRemovesUserState(t *testing.T) {
	in := []manifestFile{
		{Path: "ChillHub.exe", Size: 10},
		{Path: "config.json", Size: 45},
		{Path: "launcher.version", Size: 8},
		{Path: "runtimes/win-x64/native/blake3_dotnet.dll", Size: 20},
	}
	out := stripLauncherStateFiles("launcher", in)
	if len(out) != 2 {
		t.Fatalf("expected 2 files left, got %d: %+v", len(out), out)
	}
	for _, f := range out {
		if f.Path == "config.json" || f.Path == "launcher.version" {
			t.Fatalf("user-state file survived: %q", f.Path)
		}
	}
}

// Case and separator variations must not slip through: the launcher compares
// paths case-insensitively after normalising slashes.
func TestStripLauncherStateFilesNormalisesPaths(t *testing.T) {
	in := []manifestFile{
		{Path: "Config.JSON"},
		{Path: "\\launcher.version"},
		{Path: "/config.json"},
		{Path: "keep.dll"},
	}
	out := stripLauncherStateFiles("Launcher", in)
	if len(out) != 1 || out[0].Path != "keep.dll" {
		t.Fatalf("expected only keep.dll to survive, got %+v", out)
	}
}

// For a regular game these names are ordinary content and must be preserved:
// a game may legitimately ship its own config.json.
func TestStripLauncherStateFilesLeavesGamesAlone(t *testing.T) {
	in := []manifestFile{
		{Path: "config.json"},
		{Path: "launcher.version"},
		{Path: "game.exe"},
	}
	out := stripLauncherStateFiles("lethal-company", in)
	if len(out) != 3 {
		t.Fatalf("game manifest must be untouched, got %+v", out)
	}
}

// writeManifest is the single choke point for every publication path, so the
// invariant has to hold there rather than at the call sites.
func TestWriteManifestDropsLauncherStateFiles(t *testing.T) {
	h := New(t.TempDir())
	m := manifest{
		Version: "9.9.9",
		GameID:  LauncherGameID,
		Files: []manifestFile{
			{Path: "ChillHub.exe", Size: 1},
			{Path: "launcher.version", Size: 8},
		},
	}
	_, b, err := h.writeManifest(m, false)
	if err != nil {
		t.Fatalf("writeManifest: %v", err)
	}
	if got := string(b); contains(got, "launcher.version") {
		t.Fatalf("published manifest still contains launcher.version:\n%s", got)
	}
}

func contains(haystack, needle string) bool {
	return len(haystack) >= len(needle) && (func() bool {
		for i := 0; i+len(needle) <= len(haystack); i++ {
			if haystack[i:i+len(needle)] == needle {
				return true
			}
		}
		return false
	})()
}

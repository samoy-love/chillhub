package builds

import (
	"strings"
	"testing"
)

// sampleManifest is a small, valid manifest used as a starting point: tests
// break one thing about it and assert that validation notices.
func sampleManifest() manifest {
	return manifest{
		Version:   "1.2.3",
		BuildID:   "b-42",
		GameID:    "chill",
		CreatedAt: "2026-01-01T00:00:00Z",
		Files: []manifestFile{
			{Path: "bin/game.exe", Size: 100, Blake3: "aaaa", Sha256: "bbbb", Executable: true},
			{Path: "data/pak0.dat", Size: 200, Blake3: "cccc"},
			{Path: "readme.txt", Size: 3, Blake3: "dddd"},
		},
		EmptyDirs: []string{"logs", "saves"},
	}
}

func TestPathProblemRejectsDangerousPaths(t *testing.T) {
	bad := map[string]string{
		"parent escape":   "../evil.exe",
		"nested escape":   "data/../../evil.exe",
		"dot segment":     "data/./evil.exe",
		"absolute unix":   "/etc/passwd",
		"absolute win":    "C:/Windows/System32/evil.dll",
		"backslash":       "C:\\Windows\\evil.dll",
		"unc":             "\\\\server\\share\\evil.exe",
		"ntfs stream":     "game.exe:hidden.exe",
		"empty":           "",
		"leading slash":   "/game/app.exe",
		"trailing slash":  "game/app.exe/",
		"double slash":    "game//app.exe",
		"leading space":   " game/app.exe",
		"trailing space":  "game/app.exe ",
		"trailing dot":    "game/app.exe.",
		"tab in path":     "game/app\texe",
		"newline in path": "game/app\nexe",
		"device name":     "NUL",
		"device with ext": "data/CON.txt",
		"wildcard":        "data/*.dll",
		"too long":        strings.Repeat("a", maxPathLen+1),
	}
	for name, p := range bad {
		if why := pathProblem(p); why == "" {
			t.Errorf("%s: %q was accepted", name, p)
		}
	}

	good := []string{
		"game.exe",
		"bin/game.exe",
		"data/sub dir/pack.bin",
		"данные/игра.exe",
		"FreeTP/.hash",
		"a/b/c/d/e.dat",
	}
	for _, p := range good {
		if why := pathProblem(p); why != "" {
			t.Errorf("legitimate path %q rejected: %s", p, why)
		}
	}
}

// The client stores files in a map keyed case-insensitively and the last write
// wins, so two entries for one path mean the delivered file depends on the
// order of entries in the JSON. A manifest must describe exactly one build.
func TestDuplicatePathsAreRejected(t *testing.T) {
	cases := map[string]manifest{
		"exact duplicate": func() manifest {
			m := sampleManifest()
			m.Files = append(m.Files, manifestFile{Path: "bin/game.exe", Size: 1, Blake3: "eeee"})
			return m
		}(),
		"case-insensitive duplicate": func() manifest {
			m := sampleManifest()
			m.Files = append(m.Files, manifestFile{Path: "BIN/Game.EXE", Size: 1, Blake3: "eeee"})
			return m
		}(),
		"duplicate empty dir": func() manifest {
			m := sampleManifest()
			m.EmptyDirs = append(m.EmptyDirs, "LOGS")
			return m
		}(),
	}

	for name, m := range cases {
		if err := validateManifest(m); err == nil {
			t.Errorf("%s: validateManifest accepted it", name)
		}
	}
}

// A dangerous path must be rejected before anything is published: the manifest
// decides which executables land on the user's disk.
func TestDangerousManifestIsRejected(t *testing.T) {
	for name, path := range map[string]string{
		"traversal": "../evil.exe",
		"absolute":  "C:/Windows/System32/evil.dll",
		"startup":   "../../../../AppData/Roaming/Microsoft/Windows/Start Menu/Programs/Startup/x.exe",
	} {
		m := sampleManifest()
		m.Files[0].Path = path
		if err := validateManifest(m); err == nil {
			t.Errorf("%s: validateManifest accepted %q", name, path)
		}
	}
}

// TestManifestWithoutHashIsRejected pins the rule that a manifest entry must
// carry at least one hash.
//
// The launcher wraps its entire verification block in "if either hash is set",
// so an entry with both empty is not "integrity unknown" — it is integrity
// checking turned off for precisely the file chosen by whoever serves the
// manifest.
func TestManifestWithoutHashIsRejected(t *testing.T) {
	m := sampleManifest()
	m.Files = append(m.Files, manifestFile{Path: "payload.exe", Size: 2})
	if err := validateManifest(m); err == nil {
		t.Fatal("a manifest with a hashless entry must be rejected")
	}

	// One hash is enough.
	m.Files[len(m.Files)-1].Sha256 = "bbbb"
	if err := validateManifest(m); err != nil {
		t.Fatalf("one hash must suffice: %v", err)
	}
}

// A valid manifest must survive validation untouched — the guard above is
// worthless if it also rejects ordinary builds.
func TestValidManifestIsAccepted(t *testing.T) {
	if err := validateManifest(sampleManifest()); err != nil {
		t.Fatalf("a legitimate manifest was rejected: %v", err)
	}
}

// A trailing slash on an empty-directory entry is legitimate: "a/b/" and "a/b"
// name one directory, and the client normalises both before creating it.
//
// This is not hypothetical. Both published games carry such an entry
// (lethal-company: "BepInEx/plugins/Bertogim-LoadingScreen/",
// drive-beyond-horizons: ".../win64/FreeTP/"), and rejecting it stopped them
// from installing at all.
func TestEmptyDirTrailingSlashIsAccepted(t *testing.T) {
	m := sampleManifest()
	m.EmptyDirs = []string{
		"BepInEx/plugins/Bertogim-LoadingScreen/",
		"DriveBeyondHorizons/Plugins/SteamCorePro/Source/ThirdParty/SteamLibrary/redistributable_bin/win64/FreeTP/",
	}
	if err := validateManifest(m); err != nil {
		t.Fatalf("trailing slash on an empty dir must be accepted: %v", err)
	}

	// The leniency is exactly one slash's worth: a duplicate that differs only by
	// the trailing slash is still a duplicate.
	m.EmptyDirs = []string{"logs/", "logs"}
	if err := validateManifest(m); err == nil {
		t.Fatal("«logs/» and «logs» are one directory and must be rejected as a duplicate")
	}

	// Everything else stays strict.
	for _, bad := range []string{"../escape/", "/absolute/", "a//b/", " spaced/"} {
		m.EmptyDirs = []string{bad}
		if err := validateManifest(m); err == nil {
			t.Errorf("%q must still be rejected", bad)
		}
	}
}

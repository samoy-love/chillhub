package builds

import (
	"os"
	"path/filepath"
	"testing"
)

// promoteVersionDir must replace an existing published directory (os.Rename
// alone cannot overwrite a directory).
func TestPromoteVersionDirReplacesExisting(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	final := filepath.Join(root, "content", "game", "1.0.0")
	if err := os.MkdirAll(filepath.Join(final, "files"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(final, "files", "old.txt"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}
	stage, filesRoot, err := h.stageVersionDir("game", "1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	if stage == final {
		t.Fatal("staging dir must differ from the published dir")
	}
	if err := os.WriteFile(filepath.Join(filesRoot, "new.txt"), []byte("new"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := promoteVersionDir(stage, final); err != nil {
		t.Fatalf("promote: %v", err)
	}
	if _, err := os.Stat(filepath.Join(final, "files", "new.txt")); err != nil {
		t.Fatalf("new build not published: %v", err)
	}
	if _, err := os.Stat(filepath.Join(final, "files", "old.txt")); err == nil {
		t.Fatal("old build files survived the replacement")
	}
	if _, err := os.Stat(stage); err == nil {
		t.Fatal("staging dir still exists after promote")
	}
	assertNoBackupDirs(t, filepath.Dir(final))
}

// A failed promote must leave the previously published version in place. It
// used to RemoveAll the live directory first, so a failing rename destroyed the
// published build with nothing to restore.
func TestPromoteVersionDirRestoresOnFailure(t *testing.T) {
	root := t.TempDir()
	final := filepath.Join(root, "content", "game", "1.0.0")
	if err := os.MkdirAll(filepath.Join(final, "files"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(final, "files", "live.txt"), []byte("live"), 0o644); err != nil {
		t.Fatal(err)
	}
	// A staging directory that does not exist makes the second rename fail.
	missing := filepath.Join(root, "content", "game", "1.0.0.tmp-doesnotexist")
	if err := promoteVersionDir(missing, final); err == nil {
		t.Fatal("promote of a missing staging dir reported success")
	}
	b, err := os.ReadFile(filepath.Join(final, "files", "live.txt"))
	if err != nil {
		t.Fatalf("published version was destroyed by a failed promote: %v", err)
	}
	if string(b) != "live" {
		t.Fatalf("published content changed: %q", string(b))
	}
	assertNoBackupDirs(t, filepath.Dir(final))
}

// A backup left over by an earlier crash is swept on the next promote.
func TestPromoteVersionDirSweepsStaleBackups(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	final := filepath.Join(root, "content", "game", "1.0.0")
	stale := final + ".old-deadbeef"
	if err := os.MkdirAll(stale, 0o755); err != nil {
		t.Fatal(err)
	}
	stage, _, err := h.stageVersionDir("game", "1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	if err := promoteVersionDir(stage, final); err != nil {
		t.Fatal(err)
	}
	assertNoBackupDirs(t, filepath.Dir(final))
}

func assertNoBackupDirs(t *testing.T, parent string) {
	t.Helper()
	matches, _ := filepath.Glob(filepath.Join(parent, "*.old-*"))
	if len(matches) > 0 {
		t.Errorf("backup directories left behind: %v", matches)
	}
}

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
}

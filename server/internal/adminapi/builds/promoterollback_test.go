package builds

import (
	"errors"
	"os"
	"path/filepath"
	"testing"
)

// mustRead returns a file's contents, failing the test when it cannot.
func mustRead(t *testing.T, path string) string {
	t.Helper()
	b, err := os.ReadFile(path) // #nosec G304 -- test-local temp path
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	return string(b)
}

// seedTree writes one file into dir, creating it.
func seedTree(t *testing.T, dir, name, body string) {
	t.Helper()
	mustMkdirAll(t, dir)
	mustWriteFile(t, filepath.Join(dir, name), body)
}

// A publication is the tree AND the manifest that lists its hashes. Until the
// manifest is written the swap has to remain undoable: a manifest write that
// fails after the tree went live (a full volume is the realistic cause) used to
// leave the NEW files under the OLD manifest, with the backup already deleted.
// Every client then hashed a file the manifest did not describe, called the
// install damaged and re-downloaded it, forever.
func TestRollbackBringsThePreviousTreeBackWhenTheManifestCannotBeWritten(t *testing.T) {
	root := t.TempDir()
	final := filepath.Join(root, "content", "game", "1.0.0")
	stage := filepath.Join(root, "content", "game", ".stage")
	seedTree(t, final, "app.exe", "published build")
	seedTree(t, stage, "app.exe", "new build")

	prom, err := beginPromote(stage, final)
	if err != nil {
		t.Fatalf("beginPromote: %v", err)
	}
	if got := mustRead(t, filepath.Join(final, "app.exe")); got != "new build" {
		t.Fatalf("the new tree is not live: %q", got)
	}

	prom.Rollback(errors.New("no space left on device"))

	if got := mustRead(t, filepath.Join(final, "app.exe")); got != "published build" {
		t.Fatalf("after rollback the live tree is %q, want the previous build", got)
	}
	// No backup may be left lying next to the version: the next publication
	// sweeps them, and a stray one is gigabytes of invisible disk.
	leftovers, _ := filepath.Glob(final + ".old-*")
	if len(leftovers) != 0 {
		t.Fatalf("rollback left a backup behind: %v", leftovers)
	}
}

// A first-ever publication has nothing to restore. Undoing it must leave the
// version absent — not a half-written tree that the next request would treat as
// an installed build.
func TestRollbackOfAFirstPublicationLeavesTheVersionAbsent(t *testing.T) {
	root := t.TempDir()
	final := filepath.Join(root, "content", "game", "1.0.0")
	stage := filepath.Join(root, "content", "game", ".stage")
	seedTree(t, stage, "app.exe", "new build")

	prom, err := beginPromote(stage, final)
	if err != nil {
		t.Fatalf("beginPromote: %v", err)
	}
	prom.Rollback(errors.New("no space left on device"))

	if _, err := os.Stat(final); !os.IsNotExist(err) {
		t.Fatalf("the failed first publication left something behind: %v", err)
	}
}

// Commit is the success path: the replaced tree goes, the new one stays.
func TestCommitDropsTheReplacedTree(t *testing.T) {
	root := t.TempDir()
	final := filepath.Join(root, "content", "game", "1.0.0")
	stage := filepath.Join(root, "content", "game", ".stage")
	seedTree(t, final, "app.exe", "published build")
	seedTree(t, stage, "app.exe", "new build")

	prom, err := beginPromote(stage, final)
	if err != nil {
		t.Fatalf("beginPromote: %v", err)
	}
	prom.Commit()

	if got := mustRead(t, filepath.Join(final, "app.exe")); got != "new build" {
		t.Fatalf("live tree is %q, want the new build", got)
	}
	leftovers, _ := filepath.Glob(final + ".old-*")
	if len(leftovers) != 0 {
		t.Fatalf("commit left a backup behind: %v", leftovers)
	}
}

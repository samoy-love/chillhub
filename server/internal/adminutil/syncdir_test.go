package adminutil

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

// The directory flush added after the rename must never turn a completed write
// into a reported failure. A directory that cannot be opened for reading is the
// reachable case — and on Windows flushing a directory handle is not supported
// at all — so a branch that propagated the error would fail every publication of
// a manifest, latest.json or the games registry on those hosts, with the new
// document already on disk.
func TestWriteFileAtomicSucceedsWhenTheDirectoryCannotBeSynced(t *testing.T) {
	dir := t.TempDir()
	sub := filepath.Join(dir, "state")
	if err := os.MkdirAll(sub, 0o750); err != nil {
		t.Fatal(err)
	}
	if !denyDirReads(t, sub) {
		t.Skip("the filesystem does not honour a write-only directory here")
	}

	p := filepath.Join(sub, "games.json")
	if err := WriteFileAtomic(p, []byte(`{"v":1}`), 0o644); err != nil {
		t.Fatalf("a write whose directory cannot be synced was reported as failed: %v", err)
	}
	b, err := os.ReadFile(p) // #nosec G304 -- built from t.TempDir(), not from a request.
	if err != nil {
		t.Fatalf("the document is not on disk: %v", err)
	}
	if string(b) != `{"v":1}` {
		t.Fatalf("content = %q, want {\"v\":1}", string(b))
	}
}

// denyDirReads makes dir refuse to be opened for reading while still accepting
// new files, and reports whether it worked. Windows, and root anywhere, ignore
// the permission bits, so the caller skips instead of asserting on a state the
// platform will not produce.
func denyDirReads(t *testing.T, dir string) bool {
	t.Helper()
	if runtime.GOOS == "windows" {
		return false
	}
	st, err := os.Stat(dir)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(dir, 0o300); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chmod(dir, st.Mode().Perm()) })
	probe, err := os.Open(dir)
	if err != nil {
		return true
	}
	_ = probe.Close()
	return false
}

// A missing directory must not panic or block the caller either: the flush runs
// after the rename, and by then another process may already have moved the tree
// (a deployment swapping the content root, say).
func TestSyncDirEntryToleratesAMissingDirectory(t *testing.T) {
	syncDirEntry(filepath.Join(t.TempDir(), "gone"))
}

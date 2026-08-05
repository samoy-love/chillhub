package adminutil

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

// tempLeftovers returns the names of the temporary files WriteFileAtomic may
// have left in dir. They are the visible symptom of a half-finished write: the
// admin panel lists content directories, and a stray ".games.json.tmp-…" both
// confuses the operator and eats the disk one failed publish at a time.
func tempLeftovers(t *testing.T, dir string) []string {
	t.Helper()
	matches, err := filepath.Glob(filepath.Join(dir, ".*.tmp-*"))
	if err != nil {
		t.Fatal(err)
	}
	return matches
}

// A file in place of a parent directory is what a botched deployment looks like
// (an archive unpacked over "manifests" as a file, say). The write must fail
// with an error the caller can report, not create anything and not panic.
func TestWriteFileAtomicFailsWhenAParentIsAFile(t *testing.T) {
	dir := t.TempDir()
	blocker := filepath.Join(dir, "manifests")
	if err := os.WriteFile(blocker, []byte("not a directory"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := WriteFileAtomic(filepath.Join(blocker, "game", "latest.json"), []byte("{}"), 0o644); err == nil {
		t.Fatal("a write under a file-shaped parent reported success")
	}
	b, err := os.ReadFile(blocker) // #nosec G304 -- built from t.TempDir(), not from a request.
	if err != nil || string(b) != "not a directory" {
		t.Fatalf("the blocking file was disturbed: %q (%v)", string(b), err)
	}
}

// The rename is the last step and the only one that can fail after the payload
// is fully on disk. When it does, the temporary file must go: it is invisible to
// readers but not to the disk, and every retry of a failing publish would add
// another copy of a multi-megabyte state file.
func TestWriteFileAtomicRemovesTheTempFileWhenTheRenameFails(t *testing.T) {
	dir := t.TempDir()
	// A directory sitting where the state file belongs makes the rename fail on
	// every OS while everything before it succeeds.
	target := filepath.Join(dir, "games.json")
	if err := os.MkdirAll(target, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(target, "child"), []byte("x"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := WriteFileAtomic(target, []byte(`{"games":[]}`), 0o644); err == nil {
		t.Fatal("renaming over a directory reported success")
	}
	if leftovers := tempLeftovers(t, dir); len(leftovers) > 0 {
		t.Fatalf("temp files left behind after a failed rename: %v", leftovers)
	}
}

// A failure before the rename must leave the previously published state file
// exactly as it was. That is the whole promise of the temp-file dance: readers
// of games.json and latest.json keep seeing the old document instead of an
// empty or truncated one.
func TestWriteFileAtomicKeepsTheOldFileWhenTheWriteCannotStart(t *testing.T) {
	dir := t.TempDir()
	sub := filepath.Join(dir, "state")
	if err := os.MkdirAll(sub, 0o750); err != nil {
		t.Fatal(err)
	}
	p := filepath.Join(sub, "games.json")
	if err := WriteFileAtomic(p, []byte(`{"v":1}`), 0o644); err != nil {
		t.Fatal(err)
	}
	if !denyDirWrites(t, sub) {
		t.Skip("the filesystem does not honour a read-only directory here")
	}
	if err := WriteFileAtomic(p, []byte(`{"v":2}`), 0o644); err == nil {
		t.Fatal("a write into a read-only directory reported success")
	}
	b, err := os.ReadFile(p) // #nosec G304 -- built from t.TempDir(), not from a request.
	if err != nil {
		t.Fatalf("the previously published file is gone: %v", err)
	}
	if string(b) != `{"v":1}` {
		t.Fatalf("the previously published file changed: %q", string(b))
	}
	if leftovers := tempLeftovers(t, sub); len(leftovers) > 0 {
		t.Fatalf("temp files left behind: %v", leftovers)
	}
}

// denyDirWrites makes dir reject new files and reports whether it worked. Root,
// and Windows in general, ignore the permission bits, so the caller skips rather
// than asserts on a condition the platform will not produce.
func denyDirWrites(t *testing.T, dir string) bool {
	t.Helper()
	if runtime.GOOS == "windows" {
		return false
	}
	st, err := os.Stat(dir)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(dir, 0o500); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Chmod(dir, st.Mode().Perm()) })
	probe, err := os.CreateTemp(dir, "probe-*")
	if err != nil {
		return true
	}
	_ = probe.Close()
	_ = os.Remove(probe.Name())
	return false
}

// The mode argument decides who may read the result. Manifests and latest.json
// are served straight off disk by nginx running as another process, so a file
// created with the temp file's private 0600 would be published as a 403.
func TestWriteFileAtomicAppliesTheRequestedMode(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Windows maps only the read-only bit onto a file mode")
	}
	p := filepath.Join(t.TempDir(), "latest.json")
	if err := WriteFileAtomic(p, []byte("{}"), 0o644); err != nil {
		t.Fatal(err)
	}
	st, err := os.Stat(p)
	if err != nil {
		t.Fatal(err)
	}
	if st.Mode().Perm() != 0o644 {
		t.Fatalf("mode = %v, want 0644; os.CreateTemp's private 0600 survived", st.Mode().Perm())
	}
}

// An empty payload is a legitimate document (an emptied news index, for one) and
// must replace the old content rather than be skipped as a no-op.
func TestWriteFileAtomicWritesAnEmptyPayload(t *testing.T) {
	p := filepath.Join(t.TempDir(), "index.json")
	if err := WriteFileAtomic(p, []byte("seed"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := WriteFileAtomic(p, nil, 0o644); err != nil {
		t.Fatal(err)
	}
	b, err := os.ReadFile(p) // #nosec G304 -- built from t.TempDir(), not from a request.
	if err != nil {
		t.Fatal(err)
	}
	if len(b) != 0 {
		t.Fatalf("content = %q, want empty", string(b))
	}
}

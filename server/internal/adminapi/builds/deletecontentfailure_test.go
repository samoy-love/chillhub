package builds

import (
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// seedVersionTree lays out a published version's payload under root.
func seedVersionTree(t *testing.T, root, gid, ver string) string {
	t.Helper()
	verDir := filepath.Join(root, "content", gid, ver)
	mustMkdirAll(t, filepath.Join(verDir, "files"))
	mustWriteFile(t, filepath.Join(verDir, "files", "app.exe"), "payload")
	return verDir
}

// obstructVersionRemoval makes os.RemoveAll of verDir fail, or skips the test
// when neither obstruction bites on this platform.
//
// The two ways a delete really fails in production are platform-specific: a
// parent directory the process may not write into (POSIX) and an open handle on
// a file inside the tree (Windows). Each one is rehearsed on a throwaway copy of
// the same shape first, so this test cannot become the kind that quietly passes
// on one OS and fails on the other.
func obstructVersionRemoval(t *testing.T, verDir string) {
	t.Helper()

	// A parent directory without the write bit: POSIX refuses to unlink entries.
	probe := seedVersionTree(t, t.TempDir(), "game", "1.0.0")
	probeParent := filepath.Dir(probe)
	if os.Chmod(probeParent, 0o500) == nil {
		err := os.RemoveAll(probe)
		_ = os.Chmod(probeParent, 0o755)
		if err != nil {
			parent := filepath.Dir(verDir)
			if err := os.Chmod(parent, 0o500); err != nil {
				t.Fatalf("chmod %s: %v", parent, err)
			}
			t.Cleanup(func() { _ = os.Chmod(parent, 0o755) })
			return
		}
	}

	// An open handle on a file inside the tree: Windows refuses to delete it.
	probe2 := seedVersionTree(t, t.TempDir(), "game", "2.0.0")
	pf, err := os.Open(filepath.Join(probe2, "files", "app.exe"))
	if err != nil {
		t.Fatalf("open probe file: %v", err)
	}
	rmErr := os.RemoveAll(probe2)
	_ = pf.Close()
	if rmErr != nil {
		f, err := os.Open(filepath.Join(verDir, "files", "app.exe"))
		if err != nil {
			t.Fatalf("open payload: %v", err)
		}
		t.Cleanup(func() { _ = f.Close() })
		return
	}

	t.Skip("no obstruction on this platform makes os.RemoveAll fail")
}

// A version directory that cannot be removed used to vanish without a trace:
// the error was discarded, the answer was 200 and the gigabytes stayed on disk.
// The free-space figure the panel shows is computed from what is really left, so
// the operator saw the number disagree with the list of versions and had nothing
// in the journal to explain it.
func TestDeleteVersionReportsContentThatCouldNotBeRemoved(t *testing.T) {
	logs := captureLog(t)
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", true)
	verDir := seedVersionTree(t, root, "game", "1.0.0")
	obstructVersionRemoval(t, verDir)

	w := deleteRequest(t, h, http.MethodPost, "gameId=game&version=1.0.0")

	// The manifest is gone, so the version has disappeared for every client and
	// the request the operator made is complete: answering 500 would only send
	// the panel back to delete a version that is no longer in the list.
	if w.Code != http.StatusOK {
		t.Fatalf("%d %s, want 200", w.Code, w.Body.String())
	}
	if !strings.Contains(logs.String(), "delete content") {
		t.Fatalf("leftover content was not reported: %q", logs.String())
	}
	if _, err := os.Stat(verDir); err != nil {
		t.Fatalf("the obstruction did not hold, the tree is gone: %v", err)
	}
	// The path belongs in the journal, never in a body the panel renders.
	if strings.Contains(w.Body.String(), root) {
		t.Fatalf("the content root leaked into the response: %s", w.Body.String())
	}
}

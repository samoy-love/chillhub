package builds

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

// freeSpaceResponse issues the endpoint and decodes its body.
func freeSpaceResponse(t *testing.T, h *Handlers) (*httptest.ResponseRecorder, map[string]any) {
	t.Helper()
	w := httptest.NewRecorder()
	h.FreeSpace(w, httptest.NewRequest(http.MethodGet, "http://example.com/admin/api/freespace", nil))
	if w.Code != http.StatusOK {
		return w, nil
	}
	var out map[string]any
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatalf("not JSON: %v (%s)", err, w.Body.String())
	}
	return w, out
}

// A content root that has not been created yet is the state of a fresh
// deployment, and the panel queries free space before the first upload. The
// combined probe cannot measure a path that is not there, so the endpoint has to
// fall through to the free-bytes probe instead of reporting a broken server.
func TestFreeSpaceMeasuresAContentRootThatDoesNotExistYet(t *testing.T) {
	root := filepath.Join(t.TempDir(), "srv", "chillhub")
	h := New(root)

	w, out := freeSpaceResponse(t, h)
	if out == nil {
		t.Fatalf("%d %s", w.Code, w.Body.String())
	}
	if free, ok := out["bytes"].(float64); !ok || free <= 0 {
		t.Fatalf("bytes = %v; the panel cannot size an upload against that", out["bytes"])
	}
	if _, err := os.Stat(root); err != nil {
		t.Fatalf("the content root was not created by the probe: %v", err)
	}
}

// An empty content root means "wherever the service was started". Passing "" to
// the platform probe measures nothing at all, so it must become "." before the
// syscall — otherwise a misconfigured deployment answers 500 on an endpoint that
// could have answered honestly.
func TestFreeSpaceDefaultsToTheWorkingDirectory(t *testing.T) {
	w, out := freeSpaceResponse(t, New("   "))
	if out == nil {
		t.Fatalf("%d %s", w.Code, w.Body.String())
	}
	if free, ok := out["bytes"].(float64); !ok || free <= 0 {
		t.Fatalf("bytes = %v", out["bytes"])
	}
}

// A volume that cannot be measured must be an error, not a zero. Reporting
// "0 bytes free" would send the operator after a full disk that does not exist,
// and the panel would refuse every upload with a misleading reason.
func TestFreeSpaceReportsAnUnmeasurableVolume(t *testing.T) {
	dir := t.TempDir()
	blocker := filepath.Join(dir, "content-root")
	mustWriteFile(t, blocker, "a file where the content root should be")
	// Nothing can be created under a regular file, so neither probe can succeed.
	h := New(filepath.Join(blocker, "sub"))

	w := httptest.NewRecorder()
	h.FreeSpace(w, httptest.NewRequest(http.MethodGet, "http://example.com/admin/api/freespace", nil))
	if w.Code != http.StatusInternalServerError {
		t.Fatalf("%d %s, want 500", w.Code, w.Body.String())
	}
	if w.Body.Len() == 0 {
		t.Fatal("the failure was reported without a reason for the panel to show")
	}
}

// The response carries both numbers because the panel draws a fill bar from
// them. A total of zero is the documented "unknown" value; a total smaller than
// the free space would be nonsense the panel renders as an overflowing bar.
func TestFreeSpaceReportsATotalThatMakesSense(t *testing.T) {
	w, out := freeSpaceResponse(t, New(t.TempDir()))
	if out == nil {
		t.Fatalf("%d %s", w.Code, w.Body.String())
	}
	free, ok := out["bytes"].(float64)
	if !ok {
		t.Fatalf("bytes missing: %v", out)
	}
	total, ok := out["total"].(float64)
	if !ok {
		t.Fatalf("total missing: %v", out)
	}
	if total != 0 && total < free {
		t.Fatalf("total %v is smaller than the free space %v", total, free)
	}
}

// freeSpaceBytes creates the directory it is asked about, because the upload
// guard calls it with a staging path that does not exist yet. Without that, the
// guard would fail on every first upload of a game.
func TestFreeSpaceBytesCreatesTheProbedPath(t *testing.T) {
	p := filepath.Join(t.TempDir(), "content", "game", "1.0.0", "files")
	free, err := freeSpaceBytes(p)
	if err != nil {
		t.Fatalf("probing a not-yet-created staging path failed: %v", err)
	}
	if free == 0 {
		t.Fatal("free = 0; the guard would refuse every upload")
	}
	if st, err := os.Stat(p); err != nil || !st.IsDir() {
		t.Fatalf("the staging path was not created: %v", err)
	}
}

// An empty path measures the working directory rather than failing: the callers
// pass a path derived from the content root, which is empty in a misconfigured
// deployment, and refusing to measure would block uploads outright.
func TestFreeSpaceBytesMeasuresTheWorkingDirectoryForAnEmptyPath(t *testing.T) {
	free, err := freeSpaceBytes("")
	if err != nil {
		t.Fatalf("probing an empty path failed: %v", err)
	}
	if free == 0 {
		t.Fatal("free = 0 for the working directory")
	}
}

// When the path cannot be created, the probe measures its parent instead. The
// volume is the same either way, and that is what the caller actually asked
// about — a read-only staging parent must not be reported as a full disk.
func TestFreeSpaceBytesFallsBackToTheParentDirectory(t *testing.T) {
	parent := filepath.Join(t.TempDir(), "content")
	mustMkdirAll(t, parent)
	if !denyWritesIn(t, parent) {
		t.Skip("the filesystem does not honour a read-only directory here")
	}
	free, err := freeSpaceBytes(filepath.Join(parent, "game"))
	if err != nil {
		t.Fatalf("the probe gave up instead of measuring the parent: %v", err)
	}
	if free == 0 {
		t.Fatal("free = 0; a read-only staging parent was reported as a full disk")
	}
}

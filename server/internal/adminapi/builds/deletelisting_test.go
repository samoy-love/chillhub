package builds

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

// listVersions runs the listing handler and returns the decoded body.
func listVersions(t *testing.T, h *Handlers, gid string) (items []string, latest string) {
	t.Helper()
	w := httptest.NewRecorder()
	h.ListVersions(w, httptest.NewRequest(http.MethodGet, "http://example.com/admin/api/versions?gameId="+gid, nil))
	if w.Code != http.StatusOK {
		t.Fatalf("list: %d %s", w.Code, w.Body.String())
	}
	var out struct {
		Items []struct {
			Version string `json:"version"`
		} `json:"items"`
		Latest string `json:"latest"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatalf("list body %q: %v", w.Body.String(), err)
	}
	for _, it := range out.Items {
		items = append(items, it.Version)
	}
	return items, out.Latest
}

// Deleting must leave nothing that still promises the version: neither the
// manifest, nor the payload, nor an entry in the listing, nor a path back
// through activation. The delete endpoint once wiped the payload, answered 200
// and left the manifest behind, so the panel kept offering a version whose
// files were already gone.
func TestDeleteVersionLeavesNoTraceOfTheVersion(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", true)
	filesDir := filepath.Join(root, "content", "game", "1.0.0", "files")
	mustMkdirAll(t, filesDir)
	mustWriteFile(t, filepath.Join(filesDir, "app.exe"), "payload")

	w := deleteRequest(t, h, http.MethodPost, "gameId=game&version=1.0.0")
	if w.Code != http.StatusOK {
		t.Fatalf("%d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(h.manifestsDir("game"), "1.0.0.json")); !os.IsNotExist(err) {
		t.Fatalf("the manifest of the deleted version survived: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); !os.IsNotExist(err) {
		t.Fatalf("the version tree survived the delete: %v", err)
	}
	if items, _ := listVersions(t, h, "game"); len(items) != 0 {
		t.Fatalf("the deleted version is still listed: %v", items)
	}
	aw := httptest.NewRecorder()
	h.Activate(aw, httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/activate?gameId=game&version=1.0.0", nil))
	if aw.Code != http.StatusNotFound {
		t.Fatalf("activate after delete: %d %s, want 404", aw.Code, aw.Body.String())
	}
}

// latest.json must be repointed at a version that still exists. Leaving it
// aimed at the deleted one breaks every launcher, which reads that file on
// startup.
func TestDeleteVersionRepointsLatestAtASurvivingVersion(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", false)
	seedManifest(t, h, "game", "2.0.0", true)

	w := deleteRequest(t, h, http.MethodPost, "gameId=game&version=2.0.0")
	if w.Code != http.StatusOK {
		t.Fatalf("%d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(h.manifestsDir("game"), "2.0.0.json")); !os.IsNotExist(err) {
		t.Fatalf("the manifest of the deleted version survived: %v", err)
	}
	if got := latestVersion(t, root, "game"); got != "1.0.0" {
		t.Fatalf("latest = %q, want 1.0.0", got)
	}
}

// A manifest that lives nowhere but the inherited layout must not be found:
// the listing, the activation and the delete endpoint all read one directory
// now, and a version half-visible through one of them is worse than a version
// that is not there at all.
func TestInheritedLayoutIsNotSearched(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	legacy := filepath.Join(root, "content", "manifests", "game")
	mustMkdirAll(t, legacy)
	mustWriteFile(t, filepath.Join(legacy, "1.0.0.json"), `{"version":"1.0.0"}`)

	w := httptest.NewRecorder()
	h.ListVersions(w, httptest.NewRequest(http.MethodGet, "http://example.com/admin/api/versions?gameId=game", nil))
	if w.Code != http.StatusNotFound {
		t.Fatalf("list: %d %s, want 404", w.Code, w.Body.String())
	}
	aw := httptest.NewRecorder()
	h.Activate(aw, httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/activate?gameId=game&version=1.0.0", nil))
	if aw.Code != http.StatusNotFound {
		t.Fatalf("activate: %d %s, want 404", aw.Code, aw.Body.String())
	}
}

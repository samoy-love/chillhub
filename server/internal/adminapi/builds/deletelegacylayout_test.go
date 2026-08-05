package builds

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

// seedLegacyManifest writes a version manifest into the inherited layout, where
// the manifests live under {root}/content/manifests instead of {root}/manifests.
func seedLegacyManifest(t *testing.T, root, gid, ver, latest string) string {
	t.Helper()
	dir := filepath.Join(root, "content", "manifests", gid)
	mustMkdirAll(t, dir)
	mustWriteFile(t, filepath.Join(dir, ver+".json"), `{"version":"`+ver+`"}`)
	if latest != "" {
		mustWriteFile(t, filepath.Join(dir, "latest.json"), `{"version":"`+latest+`"}`)
	}
	return dir
}

// legacyLatestVersion reads latest.json of the inherited layout.
func legacyLatestVersion(t *testing.T, root, gid string) string {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(root, "content", "manifests", gid, "latest.json"))
	if err != nil {
		t.Fatalf("latest.json unreadable: %v", err)
	}
	var m map[string]string
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatalf("latest.json is not valid JSON (%q): %v", string(b), err)
	}
	return m["version"]
}

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

// A content root pointed at the parent directory puts the manifests under
// content/manifests, and the listing and activation handlers both look there.
// The delete handler used to look only in the primary place: it wiped the
// payload, answered 200 and left the manifest behind, so the panel kept
// offering a version whose files were already gone — the exact "the manifest
// promises what the disk no longer has" state the delete order guards against.
func TestDeleteVersionRemovesTheManifestOfTheInheritedLayout(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	manDir := seedLegacyManifest(t, root, "game", "1.0.0", "1.0.0")
	filesDir := filepath.Join(root, "content", "game", "1.0.0", "files")
	mustMkdirAll(t, filesDir)
	mustWriteFile(t, filepath.Join(filesDir, "app.exe"), "payload")

	w := deleteRequest(t, h, http.MethodPost, "gameId=game&version=1.0.0")
	if w.Code != http.StatusOK {
		t.Fatalf("%d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(manDir, "1.0.0.json")); !os.IsNotExist(err) {
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

// latest.json of the inherited layout must be repointed as well. Recomputing it
// in the primary directory instead leaves the public pointer aimed at a version
// that no longer exists, and every launcher reads that file on startup.
func TestDeleteVersionRepointsLatestOfTheInheritedLayout(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedLegacyManifest(t, root, "game", "1.0.0", "2.0.0")
	seedLegacyManifest(t, root, "game", "2.0.0", "2.0.0")

	w := deleteRequest(t, h, http.MethodPost, "gameId=game&version=2.0.0")
	if w.Code != http.StatusOK {
		t.Fatalf("%d %s", w.Code, w.Body.String())
	}
	if got := legacyLatestVersion(t, root, "game"); got != "1.0.0" {
		t.Fatalf("latest = %q, want 1.0.0", got)
	}
}

// The regression guard for the ordinary layout: resolving the manifest
// directory by the presence of the version file must not send a normal delete
// to the inherited place.
func TestDeleteVersionStillRemovesTheManifestOfTheOrdinaryLayout(t *testing.T) {
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

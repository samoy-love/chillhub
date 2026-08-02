package builds

import (
	"bytes"
	"encoding/json"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

// latestVersion reads manifests/<gid>/latest.json the way the public API serves
// it — and fails if it is not parseable, since every launcher on every machine
// reads this one file on startup.
func latestVersion(t *testing.T, root, gid string) string {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(root, "manifests", gid, "latest.json"))
	if err != nil {
		t.Fatalf("latest.json unreadable: %v", err)
	}
	var m map[string]string
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatalf("latest.json is not valid JSON (%q): %v", string(b), err)
	}
	return m["version"]
}

func seedManifest(t *testing.T, h *Handlers, gid, ver string, updateLatest bool) {
	t.Helper()
	_, _, err := h.writeManifest(manifest{
		Version: ver,
		GameID:  gid,
		Files:   []manifestFile{{Path: "app.exe", Size: 1, Blake3: "aa", Sha256: "bb"}},
	}, updateLatest)
	if err != nil {
		t.Fatalf("seed %s/%s: %v", gid, ver, err)
	}
}

// uploadRequestWithLatest publishes a game build and flips latest.json to it.
func uploadRequestWithLatest(t *testing.T, gid, ver string, zipData []byte) *http.Request {
	t.Helper()
	var body bytes.Buffer
	mw := multipart.NewWriter(&body)
	_ = mw.WriteField("kind", "game")
	_ = mw.WriteField("gameId", gid)
	_ = mw.WriteField("version", ver)
	_ = mw.WriteField("updateLatest", "1")
	fw, err := mw.CreateFormFile("zip", "build.zip")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := fw.Write(zipData); err != nil {
		t.Fatal(err)
	}
	if err := mw.Close(); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/upload", &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// latest.json is the trigger for every client update. It may only start pointing
// at a version after that version's content and manifest are both fully on disk
// — otherwise every launcher in the field asks for files that are not there yet.
func TestUploadPointsLatestAtTheVersionOnlyAfterItIsComplete(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	h.Upload(w, uploadRequestWithLatest(t, "game", "1.2.3", zipBytes(t, map[string]string{"a.txt": "x"})))
	if w.Code != http.StatusOK {
		t.Fatalf("publish failed: %d %s", w.Code, w.Body.String())
	}
	if got := latestVersion(t, root, "game"); got != "1.2.3" {
		t.Fatalf("latest = %q, want 1.2.3", got)
	}
	if _, err := os.Stat(filepath.Join(root, "manifests", "game", "1.2.3.json")); err != nil {
		t.Fatalf("latest points at a version with no manifest: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.2.3", "files", "a.txt")); err != nil {
		t.Fatalf("latest points at a version whose files are missing: %v", err)
	}
}

// A publication that fails must leave the previous release completely intact —
// content, manifest and, above all, latest.json. If a broken upload moved
// latest, every launcher would immediately try to install a version that does
// not exist.
func TestFailedPublicationLeavesTheLiveReleaseUntouched(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	w := httptest.NewRecorder()
	h.Upload(w, uploadRequestWithLatest(t, "game", "1.0.0", zipBytes(t, map[string]string{"good.txt": "v1"})))
	if w.Code != http.StatusOK {
		t.Fatalf("baseline publish failed: %d %s", w.Code, w.Body.String())
	}

	// A body that is not a ZIP at all: extraction fails after the parameters have
	// been accepted, i.e. exactly where a truncated upload dies.
	w2 := httptest.NewRecorder()
	h.Upload(w2, uploadRequestWithLatest(t, "game", "1.0.1", []byte("this is not a zip archive")))
	if w2.Code == http.StatusOK {
		t.Fatalf("a broken archive was published: %s", w2.Body.String())
	}

	if got := latestVersion(t, root, "game"); got != "1.0.0" {
		t.Errorf("latest moved to %q after a failed publication; every launcher would chase a version that was never published", got)
	}
	b, err := os.ReadFile(filepath.Join(root, "content", "game", "1.0.0", "files", "good.txt"))
	if err != nil || string(b) != "v1" {
		t.Errorf("the live release was damaged by a failed publication: %v %q", err, string(b))
	}
	if _, err := os.Stat(filepath.Join(root, "manifests", "game", "1.0.1.json")); err == nil {
		t.Error("a manifest was written for a build that never extracted")
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
	assertNoTempZip(t, root)
}

// Activating a version that does not exist must change nothing. Writing
// latest.json first and validating afterwards would point the whole install base
// at a missing manifest — a one-character typo in the admin UI would then take
// every client down until someone noticed.
func TestActivateUnknownVersionKeepsThePreviousLatest(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", true)

	w := httptest.NewRecorder()
	h.Activate(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/activate?gameId=game&version=9.9.9", nil))
	if w.Code != http.StatusNotFound {
		t.Fatalf("activate of a missing version returned %d: %s", w.Code, w.Body.String())
	}
	if got := latestVersion(t, root, "game"); got != "1.0.0" {
		t.Fatalf("latest changed to %q", got)
	}
}

// Rolling back to an older build is the emergency lever when a release turns out
// broken, so activation must be able to move latest in both directions.
func TestActivateRollsBackToAnOlderVersion(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", false)
	seedManifest(t, h, "game", "2.0.0", true)

	w := httptest.NewRecorder()
	h.Activate(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/activate?gameId=game&version=1.0.0", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("activate failed: %d %s", w.Code, w.Body.String())
	}
	if got := latestVersion(t, root, "game"); got != "1.0.0" {
		t.Fatalf("rollback did not take effect: latest = %q", got)
	}
}

// Versions are compared numerically, not as strings: 1.1.10 is newer than 1.1.9
// even though it sorts before it. The admin UI picks the "latest" row from this
// list, so a string sort silently offers the wrong build for activation.
func TestListVersionsOrdersNumerically(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	for _, v := range []string{"1.1.9", "1.1.10", "1.2.0", "1.0.2"} {
		seedManifest(t, h, "game", v, false)
	}
	seedManifest(t, h, "game", "1.1.10", true)

	w := httptest.NewRecorder()
	h.ListVersions(w, httptest.NewRequest(http.MethodGet,
		"http://example.com/admin/api/versions?gameId=game", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("list failed: %d %s", w.Code, w.Body.String())
	}
	var out struct {
		Items []struct {
			Version string `json:"version"`
		} `json:"items"`
		Latest string `json:"latest"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	var got []string
	for _, it := range out.Items {
		got = append(got, it.Version)
	}
	want := []string{"1.0.2", "1.1.9", "1.1.10", "1.2.0"}
	if len(got) != len(want) {
		t.Fatalf("got %v, want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("got %v, want %v", got, want)
		}
	}
	if out.Latest != "1.1.10" {
		t.Errorf("latest reported as %q", out.Latest)
	}
	for _, it := range out.Items {
		if it.Version == "latest" {
			t.Error("latest.json was listed as a publishable version")
		}
	}
}

// Deleting the active version must immediately repoint latest at the newest
// remaining build. Leaving it dangling means every client downloads a manifest
// that is no longer there; picking the wrong "newest" silently downgrades the
// whole install base (1.1.9 sorts after 1.1.10 as a string).
func TestDeleteActiveVersionRepointsLatestAtTheNewestRemaining(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	for _, v := range []string{"1.1.9", "1.1.10", "1.2.0"} {
		seedManifest(t, h, "game", v, false)
	}
	seedManifest(t, h, "game", "1.2.0", true)
	mustMkdirAll(t, filepath.Join(root, "content", "game", "1.2.0", "files"))

	w := httptest.NewRecorder()
	h.DeleteVersion(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/delete?gameId=game&version=1.2.0", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("delete failed: %d %s", w.Code, w.Body.String())
	}
	if got := latestVersion(t, root, "game"); got != "1.1.10" {
		t.Fatalf("latest = %q, want 1.1.10", got)
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.2.0")); err == nil {
		t.Error("the deleted version's content is still on disk")
	}
}

// Deleting a version that is not the active one must not disturb latest.json.
// Housekeeping of old builds is routine; it must never nudge the install base.
func TestDeleteInactiveVersionLeavesLatestAlone(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", false)
	seedManifest(t, h, "game", "2.0.0", true)

	w := httptest.NewRecorder()
	h.DeleteVersion(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/delete?gameId=game&version=1.0.0", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("delete failed: %d %s", w.Code, w.Body.String())
	}
	if got := latestVersion(t, root, "game"); got != "2.0.0" {
		t.Fatalf("latest = %q, want 2.0.0", got)
	}
}

// When the last version goes, latest.json has to go with it. A file that names a
// version with no manifest is worse than a missing one: the client cannot tell
// "nothing published" from "the server lost the build".
func TestDeleteLastVersionRemovesLatest(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", true)

	w := httptest.NewRecorder()
	h.DeleteVersion(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/delete?gameId=game&version=1.0.0", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("delete failed: %d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "manifests", "game", "latest.json")); !os.IsNotExist(err) {
		t.Fatalf("latest.json survived the removal of every version: %v", err)
	}
}

// Republishing an existing version must swap the content and the manifest
// together. If the manifest were updated but the content were not (or the other
// way round), clients would verify the new hashes against the old bytes and stay
// stuck in a redownload loop.
func TestRepublishingSameVersionReplacesContentAndManifestTogether(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	w := httptest.NewRecorder()
	h.Upload(w, uploadRequestWithLatest(t, "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "first"})))
	if w.Code != http.StatusOK {
		t.Fatalf("first publish failed: %d %s", w.Code, w.Body.String())
	}

	w2 := httptest.NewRecorder()
	h.Upload(w2, uploadRequestWithLatest(t, "game", "1.0.0", zipBytes(t, map[string]string{"b.txt": "second"})))
	if w2.Code != http.StatusOK {
		t.Fatalf("republish failed: %d %s", w2.Code, w2.Body.String())
	}

	files := filepath.Join(root, "content", "game", "1.0.0", "files")
	if _, err := os.Stat(filepath.Join(files, "a.txt")); err == nil {
		t.Error("a file from the replaced build is still served")
	}
	got := manifestPaths(t, w2.Body.Bytes())
	if got["a.txt"] || !got["b.txt"] {
		t.Errorf("manifest does not describe the republished tree: %v", got)
	}
	stored, err := os.ReadFile(filepath.Join(root, "manifests", "game", "1.0.0.json"))
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(bytes.TrimSpace(stored), bytes.TrimSpace(w2.Body.Bytes())) {
		t.Error("the manifest returned to the admin differs from the one published to clients")
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
}

// A publish that cannot repoint latest.json must FAIL, not report success.
//
// "Update latest" is the whole point of the request: the operator wants this
// version to be the one launchers download. If the pointer stays where it was,
// the version sits on disk unused while the panel says the build is published —
// and the discrepancy surfaces days later as "players are not getting the
// update". The files left behind are harmless: an unreferenced version
// directory is exactly what an inactive version looks like, and republishing
// overwrites it.
func TestPublishFailsWhenLatestCannotBeRepointed(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	// Publish once so that manifests/game/ exists and latest.json points at 1.0.0.
	w := httptest.NewRecorder()
	h.Upload(w, uploadRequestWithLatest(t, "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "first"})))
	if w.Code != http.StatusOK {
		t.Fatalf("setup publish returned %d: %s", w.Code, w.Body.String())
	}

	// Make latest.json impossible to replace: WriteFileAtomic renames a temp file
	// over it, and a DIRECTORY under that name cannot be replaced by a file.
	manDir := filepath.Join(root, "manifests", "game")
	latest := filepath.Join(manDir, "latest.json")
	if err := os.Remove(latest); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(latest, 0o750); err != nil {
		t.Fatal(err)
	}

	w2 := httptest.NewRecorder()
	h.Upload(w2, uploadRequestWithLatest(t, "game", "2.0.0", zipBytes(t, map[string]string{"b.txt": "second"})))

	if w2.Code == http.StatusOK {
		t.Fatalf("publish reported success while latest.json was not updated (%d)", w2.Code)
	}
	// latest.json is still the directory we planted: nothing pretended to
	// activate 2.0.0 behind the failed write.
	st, err := os.Stat(latest)
	if err != nil || !st.IsDir() {
		t.Fatalf("latest.json was replaced despite the failure: stat=%v", err)
	}
}

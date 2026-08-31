package builds

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// pruneRequest issues a mass cleanup for the given query string.
func pruneRequest(t *testing.T, h *Handlers, method, query string) *httptest.ResponseRecorder {
	t.Helper()
	w := httptest.NewRecorder()
	h.PruneVersions(w, httptest.NewRequest(method, "http://example.com/admin/api/pruneVersions?"+query, nil))
	return w
}

// pruneResult decodes the answer of a successful cleanup.
func pruneResult(t *testing.T, w *httptest.ResponseRecorder) (deleted, failed []string) {
	t.Helper()
	if w.Code != http.StatusOK {
		t.Fatalf("%d %s, want 200", w.Code, w.Body.String())
	}
	var out struct {
		Deleted []string `json:"deleted"`
		Failed  []string `json:"failed"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatalf("answer is not valid JSON (%q): %v", w.Body.String(), err)
	}
	return out.Deleted, out.Failed
}

// remainingVersions lists the manifests a game still has.
func remainingVersions(t *testing.T, h *Handlers, gid string) []string {
	t.Helper()
	entries, err := os.ReadDir(h.manifestsDir(gid))
	if err != nil {
		t.Fatalf("manifests of %s unreadable: %v", gid, err)
	}
	return manifestVersions(entries)
}

// seedVersion publishes a version together with the payload directory the
// public API serves for it, so that the cleanup can be checked against both.
func seedVersion(t *testing.T, h *Handlers, gid, ver string, active bool) {
	t.Helper()
	seedManifest(t, h, gid, ver, active)
	filesDir := filepath.Join(h.root, "content", gid, ver, "files")
	mustMkdirAll(t, filesDir)
	mustWriteFile(t, filepath.Join(filesDir, "app.exe"), "payload")
}

// The rule the button promises: the active build stays, so do the two before it
// (the rollback and its spare) and everything newer, which is an uploaded but
// not yet activated release. Everything older goes.
func TestPruneVersionsKeepsActiveTwoBeforeItAndEverythingNewer(t *testing.T) {
	h := New(t.TempDir())
	for _, v := range []string{"1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5", "1.0.6"} {
		seedVersion(t, h, "launcher", v, v == "1.0.4")
	}

	deleted, failed := pruneResult(t, pruneRequest(t, h, http.MethodPost, "gameId=launcher"))

	if strings.Join(deleted, ",") != "1.0.0,1.0.1" {
		t.Fatalf("deleted = %v, want [1.0.0 1.0.1]", deleted)
	}
	if len(failed) != 0 {
		t.Fatalf("failed = %v, want none", failed)
	}
	left := strings.Join(remainingVersions(t, h, "launcher"), ",")
	if left != "1.0.2,1.0.3,1.0.4,1.0.5,1.0.6" {
		t.Fatalf("remaining = %s, want 1.0.2..1.0.6", left)
	}
	// The active pointer still names a version that is on disk.
	if got := latestVersion(t, h.root, "launcher"); got != "1.0.4" {
		t.Fatalf("latest = %q, want 1.0.4", got)
	}
}

// The payload is the reason the button exists: leaving content/{gid}/{ver}
// behind frees nothing, and the free-space figure in the panel is computed from
// what is actually on disk.
func TestPruneVersionsRemovesTheContentOfEveryDeletedVersion(t *testing.T) {
	h := New(t.TempDir())
	for _, v := range []string{"1.0.0", "1.0.1", "1.0.2", "1.0.3"} {
		seedVersion(t, h, "launcher", v, v == "1.0.3")
	}

	pruneResult(t, pruneRequest(t, h, http.MethodPost, "gameId=launcher"))

	if _, err := os.Stat(filepath.Join(h.root, "content", "launcher", "1.0.0")); !os.IsNotExist(err) {
		t.Fatalf("the payload of a deleted version survived: %v", err)
	}
	for _, v := range []string{"1.0.1", "1.0.2", "1.0.3"} {
		if _, err := os.Stat(filepath.Join(h.root, "content", "launcher", v, "files", "app.exe")); err != nil {
			t.Fatalf("the payload of kept version %s was removed: %v", v, err)
		}
	}
}

// Version order is numeric, not textual: as text 1.1.10 sorts before 1.1.9, and
// a cleanup that believed that would delete the newest builds and keep the
// oldest.
func TestPruneVersionsCountsVersionsNumerically(t *testing.T) {
	h := New(t.TempDir())
	for _, v := range []string{"1.1.8", "1.1.9", "1.1.10", "1.1.11"} {
		seedVersion(t, h, "launcher", v, v == "1.1.11")
	}

	deleted, _ := pruneResult(t, pruneRequest(t, h, http.MethodPost, "gameId=launcher"))

	if strings.Join(deleted, ",") != "1.1.8" {
		t.Fatalf("deleted = %v, want [1.1.8]", deleted)
	}
}

// Fewer builds than the rule keeps is the normal state right after a release;
// the button must then do nothing at all rather than reach past the active one.
func TestPruneVersionsDeletesNothingWhenOnlyTheKeptVersionsExist(t *testing.T) {
	h := New(t.TempDir())
	for _, v := range []string{"1.0.0", "1.0.1", "1.0.2"} {
		seedVersion(t, h, "launcher", v, v == "1.0.2")
	}

	deleted, _ := pruneResult(t, pruneRequest(t, h, http.MethodPost, "gameId=launcher"))

	if len(deleted) != 0 {
		t.Fatalf("deleted = %v, want none", deleted)
	}
	if left := remainingVersions(t, h, "launcher"); len(left) != 3 {
		t.Fatalf("remaining = %v, want all three", left)
	}
}

// Without a working latest.json there is no point to count "old" from. Deleting
// nothing and answering 200 would read in the panel as "already clean", so the
// broken state is reported instead.
func TestPruneVersionsRefusesWithoutAnActiveVersion(t *testing.T) {
	cases := []struct {
		name string
		// latestJSON — содержимое latest.json; пустая строка означает, что файла
		// нет вовсе.
		latestJSON string
	}{
		{"no latest.json at all", ""},
		{"latest.json names a version that is gone", `{"version":"9.9.9"}`},
		{"latest.json is not readable as JSON", "not json at all"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			h := New(t.TempDir())
			for _, v := range []string{"1.0.0", "1.0.1", "1.0.2", "1.0.3"} {
				seedVersion(t, h, "launcher", v, false)
			}
			if tc.latestJSON != "" {
				mustWriteFile(t, filepath.Join(h.manifestsDir("launcher"), "latest.json"), tc.latestJSON)
			}

			w := pruneRequest(t, h, http.MethodPost, "gameId=launcher")

			if w.Code != http.StatusConflict {
				t.Fatalf("%d %s, want 409", w.Code, w.Body.String())
			}
			if left := remainingVersions(t, h, "launcher"); len(left) != 4 {
				t.Fatalf("a refused cleanup still deleted versions: %v", left)
			}
		})
	}
}

// One stuck version must not swallow the rest of the work, and it must not be
// reported as deleted either — the panel names it, so the operator knows what is
// still occupying the disk.
func TestPruneVersionsReportsTheVersionsItCouldNotRemove(t *testing.T) {
	h := New(t.TempDir())
	for _, v := range []string{"1.0.0", "1.0.1", "1.0.2", "1.0.3", "1.0.4"} {
		seedVersion(t, h, "launcher", v, v == "1.0.4")
	}
	// A non-empty directory in place of the manifest file cannot be os.Remove'd,
	// and the failure is not "not exists" — exactly the case the handler must
	// report rather than count as done.
	blocked := filepath.Join(h.manifestsDir("launcher"), "1.0.1.json")
	if err := os.Remove(blocked); err != nil {
		t.Fatalf("cannot replace the manifest with a directory: %v", err)
	}
	mustMkdirAll(t, blocked)
	mustWriteFile(t, filepath.Join(blocked, "child"), "x")

	deleted, failed := pruneResult(t, pruneRequest(t, h, http.MethodPost, "gameId=launcher"))

	if strings.Join(deleted, ",") != "1.0.0" {
		t.Fatalf("deleted = %v, want [1.0.0]", deleted)
	}
	if strings.Join(failed, ",") != "1.0.1" {
		t.Fatalf("failed = %v, want [1.0.1]", failed)
	}
	// The payload of a version that could not be unpublished stays: its manifest
	// is still there, and clients are still being offered those files.
	if _, err := os.Stat(filepath.Join(h.root, "content", "launcher", "1.0.1", "files", "app.exe")); err != nil {
		t.Fatalf("the payload went while the manifest stayed: %v", err)
	}
}

// Wiping most of a game's history is irreversible, so it must not be reachable
// by a request a browser can be tricked into issuing on its own.
func TestPruneVersionsRefusesNonPostMethods(t *testing.T) {
	h := New(t.TempDir())
	for _, v := range []string{"1.0.0", "1.0.1", "1.0.2", "1.0.3"} {
		seedVersion(t, h, "launcher", v, v == "1.0.3")
	}

	for _, method := range []string{http.MethodGet, http.MethodPut, http.MethodDelete} {
		w := pruneRequest(t, h, method, "gameId=launcher")
		if w.Code != http.StatusMethodNotAllowed {
			t.Errorf("%s: %d, want 405", method, w.Code)
		}
	}
	if left := remainingVersions(t, h, "launcher"); len(left) != 4 {
		t.Fatalf("a rejected request still deleted versions: %v", left)
	}
}

// An empty gameId must never be read as "the whole content root", and a
// traversal must never be turned into a path.
func TestPruneVersionsRejectsMissingAndUnsafeGameID(t *testing.T) {
	for _, query := range []string{"", "gameId=", "gameId=../../etc", "gameId=launcher/../game"} {
		h := New(t.TempDir())
		w := pruneRequest(t, h, http.MethodPost, query)
		if w.Code != http.StatusBadRequest {
			t.Errorf("%q: %d %s, want 400", query, w.Code, w.Body.String())
		}
	}
}

// An unknown game answers 404 without echoing the absolute content root, which
// the panel would show verbatim.
func TestPruneVersionsHidesThePathOfAnUnknownGame(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	w := pruneRequest(t, h, http.MethodPost, "gameId=nosuchgame")

	if w.Code != http.StatusNotFound {
		t.Fatalf("%d %s, want 404", w.Code, w.Body.String())
	}
	if strings.Contains(w.Body.String(), root) {
		t.Fatalf("the content root leaked into the error: %s", w.Body.String())
	}
}

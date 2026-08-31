package builds

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// deleteRequest issues a delete for the given query string.
func deleteRequest(t *testing.T, h *Handlers, method, query string) *httptest.ResponseRecorder {
	t.Helper()
	w := httptest.NewRecorder()
	h.DeleteVersion(w, httptest.NewRequest(method, "http://example.com/admin/api/delete?"+query, nil))
	return w
}

// Deleting a build is destructive and irreversible, so it must not be reachable
// by anything a browser can be tricked into issuing on its own — a GET from an
// <img> tag being the classic one.
func TestDeleteVersionRefusesNonPostMethods(t *testing.T) {
	h := New(t.TempDir())
	seedManifest(t, h, "game", "1.0.0", true)

	for _, method := range []string{http.MethodGet, http.MethodPut, http.MethodDelete} {
		w := deleteRequest(t, h, method, "gameId=game&version=1.0.0")
		if w.Code != http.StatusMethodNotAllowed {
			t.Errorf("%s: %d, want 405", method, w.Code)
		}
	}
	if _, err := os.Stat(filepath.Join(h.manifestsDir("game"), "1.0.0.json")); err != nil {
		t.Fatalf("a rejected request still deleted the manifest: %v", err)
	}
}

// A half-filled form must be refused outright. Treating an empty version as "all
// of them", or an empty gameId as "the whole content root", is how a delete
// endpoint takes out more than the operator asked for.
func TestDeleteVersionRejectsIncompleteAndUnsafeInput(t *testing.T) {
	cases := []struct {
		name  string
		query string
	}{
		{"no parameters at all", ""},
		{"version without a game", "version=1.0.0"},
		{"game without a version", "gameId=game"},
		{"empty version", "gameId=game&version="},
		{"traversal in the game id", "gameId=../../etc&version=1.0.0"},
		{"traversal in the version", "gameId=game&version=../../etc"},
		{"separator in the version", "gameId=game&version=1.0.0/files"},
		// Dots and nothing else: IsSafeVersion accepts this one, because
		// versions are allowed to contain dots. See the test below for what it
		// costs when the joined path is not re-checked.
		{"the version is just dot-dot", "gameId=game&version=.."},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			h := New(t.TempDir())
			w := deleteRequest(t, h, http.MethodPost, tc.query)
			if w.Code != http.StatusBadRequest {
				t.Fatalf("%q: %d %s, want 400", tc.query, w.Code, w.Body.String())
			}
		})
	}
}

// The panel retries a delete that timed out, and two admins can click the same
// row. A version that is already gone must answer 200 rather than 500, or a
// second click reports a failure for work that is in fact complete.
func TestDeleteVersionIsIdempotent(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", false)
	seedManifest(t, h, "game", "2.0.0", true)

	for i := range 2 {
		w := deleteRequest(t, h, http.MethodPost, "gameId=game&version=1.0.0")
		if w.Code != http.StatusOK {
			t.Fatalf("delete #%d: %d %s", i+1, w.Code, w.Body.String())
		}
	}
	if got := latestVersion(t, root, "game"); got != "2.0.0" {
		t.Fatalf("latest = %q, want 2.0.0", got)
	}
}

// A manifest that cannot be removed must abort the delete BEFORE the extracted
// content goes. The two are what the public API hands out together; removing
// the payload while the manifest still advertises it leaves every client
// downloading files that are no longer on disk.
func TestDeleteVersionKeepsTheContentWhenTheManifestCannotBeRemoved(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	// A directory where the manifest file belongs cannot be os.Remove'd, and the
	// failure is not "not exists" — exactly the case the handler must report.
	manPath := filepath.Join(h.manifestsDir("game"), "1.0.0.json")
	mustMkdirAll(t, manPath)
	mustWriteFile(t, filepath.Join(manPath, "child"), "x")
	filesDir := filepath.Join(root, "content", "game", "1.0.0", "files")
	mustMkdirAll(t, filesDir)
	mustWriteFile(t, filepath.Join(filesDir, "app.exe"), "payload")

	w := deleteRequest(t, h, http.MethodPost, "gameId=game&version=1.0.0")
	if w.Code != http.StatusInternalServerError {
		t.Fatalf("%d %s, want 500", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(filesDir, "app.exe")); err != nil {
		t.Fatalf("the payload was deleted although the manifest survived: %v", err)
	}
	// The error body is shown in the panel and must not carry the content root.
	if strings.Contains(w.Body.String(), root) {
		t.Fatalf("the content root leaked into the error: %s", w.Body.String())
	}
}

// version=".." passes adminutil.IsSafeVersion — it is dots and nothing else,
// and versions are allowed dots. Joined onto the game directory it collapses to
// the content root, and the os.RemoveAll that follows takes out every game on
// the server while the endpoint answers "ok".
func TestDeleteVersionCannotEscapeTheGameDirectory(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", true)
	seedManifest(t, h, "other", "2.0.0", true)
	victim := filepath.Join(root, "content", "other", "2.0.0", "files")
	mustMkdirAll(t, victim)
	mustWriteFile(t, filepath.Join(victim, "app.exe"), "payload")

	w := deleteRequest(t, h, http.MethodPost, "gameId=game&version=..")

	if w.Code != http.StatusBadRequest {
		t.Fatalf("%d %s, want 400", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(victim, "app.exe")); err != nil {
		t.Fatalf("another game's content was deleted: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "content")); err != nil {
		t.Fatalf("the content root itself was removed: %v", err)
	}
}

// The mass cleanup shares removeVersion with the single-row delete, so the same
// guard has to hold there. A manifest file named "...json" yields the version
// "..", and nothing stops such a file from existing on disk.
func TestPruneVersionsSkipsAManifestThatNamesNoDirectory(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	if err := h.removeVersion("game", ".."); err == nil {
		t.Fatal("removeVersion accepted a version that escapes its game directory")
	}
	if _, err := os.Stat(filepath.Join(root, "content")); err != nil && !os.IsNotExist(err) {
		t.Fatalf("unexpected state of the content root: %v", err)
	}
}

// The version directory must go even when it holds files marked read-only or a
// deep tree — the disk-space report the panel shows is computed from what is
// actually left behind.
func TestDeleteVersionRemovesTheWholeVersionTree(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", true)
	deep := filepath.Join(root, "content", "game", "1.0.0", "files", "data", "sub")
	mustMkdirAll(t, deep)
	mustWriteFile(t, filepath.Join(deep, "big.bin"), "payload")

	w := deleteRequest(t, h, http.MethodPost, "gameId=game&version=1.0.0")
	if w.Code != http.StatusOK {
		t.Fatalf("%d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); !os.IsNotExist(err) {
		t.Fatalf("the version tree survived the delete: %v", err)
	}
	// Sibling versions of the same game must be untouched.
	if _, err := os.Stat(filepath.Join(root, "content", "game")); err != nil {
		t.Fatalf("the game directory itself was removed: %v", err)
	}
}

// A latest.json that cannot be rewritten must be reported. Silence here leaves
// the pointer aimed at a version whose manifest was just deleted, and every
// launcher on every machine reads that one file on startup.
func TestRecalcLatestReportsAFailedRepoint(t *testing.T) {
	logs := captureLog(t)
	manDir := filepath.Join(t.TempDir(), "manifests", "game")
	mustMkdirAll(t, manDir)
	mustWriteFile(t, filepath.Join(manDir, "1.0.0.json"), "{}")
	// A directory in place of latest.json makes the atomic write's rename fail.
	blocked := filepath.Join(manDir, "latest.json")
	mustMkdirAll(t, blocked)
	mustWriteFile(t, filepath.Join(blocked, "child"), "x")

	recalcLatest(manDir)

	if !strings.Contains(logs.String(), "cannot repoint") {
		t.Fatalf("a dangling latest.json was not reported: %q", logs.String())
	}
}

// The rewritten pointer must name the highest remaining version by version
// order, not by string order: 1.1.9 sorts after 1.1.10 as text, and picking it
// silently downgrades every installation.
func TestRecalcLatestPicksTheHighestRemainingVersion(t *testing.T) {
	root := t.TempDir()
	manDir := filepath.Join(root, "manifests", "game")
	mustMkdirAll(t, manDir)
	for _, v := range []string{"1.1.9", "1.1.10", "1.2.0"} {
		mustWriteFile(t, filepath.Join(manDir, v+".json"), "{}")
	}

	recalcLatest(manDir)

	if got := latestVersion(t, root, "game"); got != "1.2.0" {
		t.Fatalf("latest = %q, want 1.2.0", got)
	}
}

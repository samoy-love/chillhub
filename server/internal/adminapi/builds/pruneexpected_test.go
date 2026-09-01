package builds

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// pruneNaming issues a mass cleanup for one game, naming the versions the panel
// showed the operator.
func pruneNaming(t *testing.T, h *Handlers, gid string, expected ...string) *httptest.ResponseRecorder {
	t.Helper()
	q := url.Values{"gameId": {gid}}
	for _, v := range expected {
		q.Add("expected", v)
	}
	w := httptest.NewRecorder()
	h.PruneVersions(w, httptest.NewRequest(http.MethodPost, "http://example.com/admin/pruneVersions?"+q.Encode(), nil))
	return w
}

// seedPrunable lays out five versions with 1.0.5 active, so 1.0.1 and 1.0.2 are
// old enough to be swept and 1.0.3/1.0.4 are the two kept before the active one.
func seedPrunable(t *testing.T, h *Handlers) {
	t.Helper()
	for _, v := range []string{"1.0.1", "1.0.2", "1.0.3", "1.0.4", "1.0.5"} {
		seedManifest(t, h, "game", v, v == "1.0.5")
	}
}

// The operator agreed to a NAMED list. If the set moved between the dialog and
// the click — another tab or CI uploaded and activated a build, so the cut
// slides by one — the request must be refused, not silently applied to the new
// set: the version the dialog promised to keep would otherwise be gone for good.
func TestPruneRefusesWhenTheSetMovedSinceTheDialog(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedPrunable(t, h)

	// The panel showed only 1.0.1; meanwhile 1.0.2 also became old.
	w := pruneNaming(t, h, "game", "1.0.1")

	if w.Code != http.StatusConflict {
		t.Fatalf("%d %s, want 409", w.Code, w.Body.String())
	}
	for _, v := range []string{"1.0.1", "1.0.2"} {
		if _, err := os.Stat(filepath.Join(h.manifestsDir("game"), v+".json")); err != nil {
			t.Fatalf("a refused cleanup still deleted %s: %v", v, err)
		}
	}
	// The body is shown in the panel and must not carry the content root.
	if strings.Contains(w.Body.String(), root) {
		t.Fatalf("the content root leaked into the error: %s", w.Body.String())
	}
}

// The matching case still works, and the answer names what went.
func TestPruneRunsWhenTheNamedSetStillMatches(t *testing.T) {
	h := New(t.TempDir())
	seedPrunable(t, h)

	w := pruneNaming(t, h, "game", "1.0.1", "1.0.2")

	if w.Code != http.StatusOK {
		t.Fatalf("%d %s, want 200", w.Code, w.Body.String())
	}
	for _, v := range []string{"1.0.1", "1.0.2"} {
		if _, err := os.Stat(filepath.Join(h.manifestsDir("game"), v+".json")); !os.IsNotExist(err) {
			t.Fatalf("%s survived the cleanup it was named in: %v", v, err)
		}
	}
	for _, v := range []string{"1.0.3", "1.0.4", "1.0.5"} {
		if _, err := os.Stat(filepath.Join(h.manifestsDir("game"), v+".json")); err != nil {
			t.Fatalf("%s was kept by the rule but deleted anyway: %v", v, err)
		}
	}
}

// Order is not part of the agreement — membership is. The panel sorts ascending
// and so does the server, but a future change to either must not turn into a
// refusal that reads as "somebody uploaded a build".
func TestPruneComparesSetsNotOrder(t *testing.T) {
	h := New(t.TempDir())
	seedPrunable(t, h)

	if w := pruneNaming(t, h, "game", "1.0.2", "1.0.1"); w.Code != http.StatusOK {
		t.Fatalf("%d %s, want 200", w.Code, w.Body.String())
	}
}

// A panel that does not send the list at all keeps working: the parameter had to
// be optional, or every copy of the page already open would break on deploy.
func TestPruneWithoutTheNamedSetBehavesAsBefore(t *testing.T) {
	h := New(t.TempDir())
	seedPrunable(t, h)

	if w := pruneNaming(t, h, "game"); w.Code != http.StatusOK {
		t.Fatalf("%d %s, want 200", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(h.manifestsDir("game"), "1.0.1.json")); !os.IsNotExist(err) {
		t.Fatalf("1.0.1 survived a cleanup with no expectations: %v", err)
	}
}

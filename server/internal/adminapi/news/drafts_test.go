package news

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// publicTree lists every file under content/news, which nginx (and the dev
// static handler in cmd/api) hand out verbatim.
func publicTree(t *testing.T, root string) []string {
	t.Helper()
	base := filepath.Join(root, "news")
	var out []string
	_ = filepath.WalkDir(base, func(p string, d os.DirEntry, err error) error {
		if err != nil || d.IsDir() {
			return nil
		}
		rel, _ := filepath.Rel(base, p)
		out = append(out, filepath.ToSlash(rel))
		return nil
	})
	return out
}

func saveArticle(t *testing.T, h *Handlers, slug, md string, published bool) {
	t.Helper()
	fields := map[string]string{
		"scope":     "launcher",
		"slug":      slug,
		"markdown":  md,
		"published": map[bool]string{true: "true", false: "false"}[published],
	}
	w := httptest.NewRecorder()
	h.Save(w, multipartForm(t, "http://example.com/admin/news/save", fields))
	if w.Code != http.StatusOK {
		t.Fatalf("save %s: %d %s", slug, w.Code, w.Body.String())
	}
}

// An unpublished article and the flag map must not exist anywhere under
// content/news: that whole directory is served statically, so storing drafts
// there hands them out at GET /news/<slug>.md and GET /news/news_meta.json.
func TestDraftsAreNotInThePublicTree(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "draft-one", "# secret draft\n\nunreleased", false)
	saveArticle(t, h, "live-one", "# live\n\npublic", true)

	files := publicTree(t, root)
	for _, f := range files {
		if f == "draft-one.md" {
			t.Errorf("draft markdown is publicly served: %s", f)
		}
		if strings.HasSuffix(f, "news_meta.json") {
			t.Errorf("draft flag map is publicly served: %s", f)
		}
	}
	if _, err := os.Stat(filepath.Join(root, "news", "live-one.md")); err != nil {
		t.Fatalf("published article is not in the served tree: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "news_private", "draft-one.md")); err != nil {
		t.Fatalf("draft was not stored privately: %v", err)
	}

	// The publicly served index must not list the draft either.
	b, err := os.ReadFile(filepath.Join(root, "news", "index.json"))
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(b), "draft-one") {
		t.Errorf("public index.json leaks the draft: %s", string(b))
	}
	if !strings.Contains(string(b), "live-one") {
		t.Errorf("public index.json is missing the published article: %s", string(b))
	}
}

// The admin list keeps its contract: every article, drafts included.
func TestAdminListStillSeesDrafts(t *testing.T) {
	h, _ := newHandlers(t)
	saveArticle(t, h, "draft-one", "# draft", false)
	saveArticle(t, h, "live-one", "# live", true)

	w := httptest.NewRecorder()
	h.List(w, httptest.NewRequest(http.MethodGet, "http://example.com/admin/news/list?scope=launcher", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("list: %d %s", w.Code, w.Body.String())
	}
	var idx struct {
		Items []struct {
			Slug      string `json:"slug"`
			Published bool   `json:"published"`
		} `json:"items"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &idx); err != nil {
		t.Fatalf("list json: %v (%s)", err, w.Body.String())
	}
	got := map[string]bool{}
	for _, it := range idx.Items {
		got[it.Slug] = it.Published
	}
	if len(idx.Items) != 2 || got["draft-one"] != false || got["live-one"] != true {
		t.Fatalf("admin list lost the draft view: %+v", idx.Items)
	}
}

// Publishing moves the markdown into the served tree; unpublishing takes it
// back out.
func TestPublishMovesArticleBetweenTrees(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "story", "# story", false)

	pubPath := filepath.Join(root, "news", "story.md")
	privPath := filepath.Join(root, "news_private", "story.md")

	publish := func(v string) {
		w := httptest.NewRecorder()
		h.Publish(w, urlencodedForm(t, "http://example.com/admin/news/publish", url.Values{
			"scope":     {"launcher"},
			"slug":      {"story"},
			"published": {v},
		}))
		if w.Code != http.StatusOK {
			t.Fatalf("publish=%s: %d %s", v, w.Code, w.Body.String())
		}
	}

	publish("true")
	if _, err := os.Stat(pubPath); err != nil {
		t.Fatalf("published article not moved into the served tree: %v", err)
	}
	if _, err := os.Stat(privPath); err == nil {
		t.Fatal("private copy survived publication")
	}

	publish("false")
	if _, err := os.Stat(pubPath); err == nil {
		t.Fatal("unpublished article is still served")
	}
	if _, err := os.Stat(privPath); err != nil {
		t.Fatalf("unpublished article not moved back: %v", err)
	}
}

// Content written by an older build (drafts and news_meta.json inside
// content/news) must be migrated out of the served tree on the next rebuild.
func TestRebuildMigratesLegacyLayout(t *testing.T) {
	h, root := newHandlers(t)
	base := filepath.Join(root, "news")
	if err := os.MkdirAll(base, 0o755); err != nil {
		t.Fatal(err)
	}
	for name, body := range map[string]string{
		"old-draft.md": "# old draft",
		"old-live.md":  "# old live",
	} {
		if err := os.WriteFile(filepath.Join(base, name), []byte(body), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	legacyMeta := `{"old-draft":{"published":false,"coverUrl":""},"old-live":{"published":true,"coverUrl":""}}`
	if err := os.WriteFile(filepath.Join(base, "news_meta.json"), []byte(legacyMeta), 0o644); err != nil {
		t.Fatal(err)
	}

	w := httptest.NewRecorder()
	h.Rebuild(w, httptest.NewRequest(http.MethodPost, "http://example.com/admin/news/rebuild?scope=launcher", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("rebuild: %d %s", w.Code, w.Body.String())
	}

	if _, err := os.Stat(filepath.Join(base, "old-draft.md")); err == nil {
		t.Error("legacy draft is still publicly served")
	}
	if _, err := os.Stat(filepath.Join(base, "news_meta.json")); err == nil {
		t.Error("legacy news_meta.json is still publicly served")
	}
	if _, err := os.Stat(filepath.Join(root, "news_private", "old-draft.md")); err != nil {
		t.Errorf("legacy draft was lost instead of moved: %v", err)
	}
	if _, err := os.Stat(filepath.Join(base, "old-live.md")); err != nil {
		t.Errorf("published article must stay served: %v", err)
	}
	// The flags survived the migration.
	w = httptest.NewRecorder()
	h.Get(w, httptest.NewRequest(http.MethodGet,
		"http://example.com/admin/news/get?scope=launcher&slug=old-draft", nil))
	if w.Code != http.StatusOK || !strings.Contains(w.Body.String(), `"published":false`) {
		t.Errorf("draft metadata lost: %d %s", w.Code, w.Body.String())
	}
}

// Per-game news gets the same treatment.
func TestGameScopeDraftsAreNotPublic(t *testing.T) {
	h, root := newHandlers(t)
	w := httptest.NewRecorder()
	h.Save(w, multipartForm(t, "http://example.com/admin/news/save", map[string]string{
		"scope":     "game",
		"gameId":    "mygame",
		"slug":      "game-draft",
		"markdown":  "# hidden",
		"published": "false",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("save: %d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "news", "games", "mygame", "game-draft.md")); err == nil {
		t.Error("per-game draft is publicly served")
	}
	if _, err := os.Stat(filepath.Join(root, "news_private", "games", "mygame", "game-draft.md")); err != nil {
		t.Errorf("per-game draft was not stored privately: %v", err)
	}
}

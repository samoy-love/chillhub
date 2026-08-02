package news

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"sync"
	"testing"
)

// Two admins saving different articles at the same time must both survive. The
// read-modify-write of news_meta.json had no lock, so the second writer read the
// metadata before the first had written it and dropped the first one's entry.
//
// Run with -race to also catch the concurrent map access this used to allow.
func TestConcurrentSavesKeepEveryArticle(t *testing.T) {
	h, _ := newHandlers(t)
	// One letter per goroutine: the slug has to differ per save, and indexing a
	// string keeps it a plain ASCII suffix.
	const slugLetters = "abcdefghijkl"
	const n = len(slugLetters)

	var wg sync.WaitGroup
	for i := range n {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			req := multipartForm(t, "http://example.com/admin/news/save", map[string]string{
				"scope":     "launcher",
				"slug":      "article-" + slugLetters[i:i+1],
				"markdown":  "# article",
				"published": "true",
			})
			w := httptest.NewRecorder()
			h.Save(w, req)
			if w.Code != http.StatusOK {
				t.Errorf("save %d: %d %s", i, w.Code, w.Body.String())
			}
		}(i)
	}
	wg.Wait()

	w := httptest.NewRecorder()
	h.List(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://example.com/admin/news/list?scope=launcher", nil))
	var idx struct {
		Items []struct {
			Slug      string `json:"slug"`
			Published bool   `json:"published"`
		} `json:"items"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &idx); err != nil {
		t.Fatalf("list json: %v (%s)", err, w.Body.String())
	}
	if len(idx.Items) != n {
		t.Fatalf("index lists %d of %d articles: concurrent saves lost updates", len(idx.Items), n)
	}
	for _, it := range idx.Items {
		if !it.Published {
			t.Errorf("%s lost its published flag", it.Slug)
		}
	}
}

// Concurrent publish toggles must not lose entries in news_meta.json either.
func TestConcurrentPublishKeepsMetadata(t *testing.T) {
	h, root := newHandlers(t)
	slugs := []string{"one", "two", "three", "four", "five", "six"}
	for _, s := range slugs {
		saveArticle(t, h, s, "# "+s, false)
	}

	var wg sync.WaitGroup
	for _, s := range slugs {
		wg.Add(1)
		go func(s string) {
			defer wg.Done()
			w := httptest.NewRecorder()
			h.Publish(w, urlencodedForm(t, "http://example.com/admin/news/publish", map[string][]string{
				"scope":     {"launcher"},
				"slug":      {s},
				"published": {"true"},
			}))
			if w.Code != http.StatusOK {
				t.Errorf("publish %s: %d", s, w.Code)
			}
		}(s)
	}
	wg.Wait()

	b := readFile(t, filepath.Join(root, "news_private", "news_meta.json"))
	var m map[string]meta
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatalf("news_meta.json is not valid JSON after concurrent writes: %v (%s)", err, string(b))
	}
	for _, s := range slugs {
		if !m[s].Published {
			t.Errorf("%s lost its published flag: %+v", s, m)
		}
	}
}

// Every state file the public API reads must be written atomically: a reader
// must never see a truncated document.
func TestIndexAndMetaAreWrittenAtomically(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "live", "# live", true)

	for _, p := range []string{
		filepath.Join(root, "news", "index.json"),
		filepath.Join(root, "news_private", "index.json"),
		filepath.Join(root, "news_private", "news_meta.json"),
	} {
		b := readFile(t, p)
		var v any
		if err := json.Unmarshal(b, &v); err != nil {
			t.Errorf("%s is not valid JSON: %v", p, err)
		}
	}
	// No temp files left behind by the atomic writer.
	for _, dir := range []string{filepath.Join(root, "news"), filepath.Join(root, "news_private")} {
		entries, err := os.ReadDir(dir)
		if err != nil {
			continue
		}
		for _, e := range entries {
			if matched, _ := filepath.Match("*.tmp-*", e.Name()); matched {
				t.Errorf("temp file left behind: %s", filepath.Join(dir, e.Name()))
			}
		}
	}
}

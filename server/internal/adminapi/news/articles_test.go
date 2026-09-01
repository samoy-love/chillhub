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
	"unicode/utf8"
)

// readFile reads a file the test itself put under its own t.TempDir().
func readFile(t *testing.T, path string) []byte {
	t.Helper()
	// #nosec G304 -- path is built by the test from the t.TempDir() it created.
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	return b
}

// markdownOf pulls the markdown field out of a decoded Get answer.
func markdownOf(t *testing.T, got map[string]any) string {
	t.Helper()
	md, ok := got["markdown"].(string)
	if !ok {
		t.Fatalf("the answer carries no markdown: %v", got)
	}
	return md
}

// readIndex loads one of the two index.json files as a slug -> item map.
func readIndex(t *testing.T, path string) map[string]newsItem {
	t.Helper()
	b := readFile(t, path)
	var idx struct {
		Items []newsItem `json:"items"`
	}
	if err := json.Unmarshal(b, &idx); err != nil {
		t.Fatalf("%s is not valid JSON: %v (%s)", path, err, string(b))
	}
	out := map[string]newsItem{}
	for _, it := range idx.Items {
		out[it.Slug] = it
	}
	return out
}

func publicIndex(t *testing.T, root string) map[string]newsItem {
	t.Helper()
	return readIndex(t, filepath.Join(root, "news", "index.json"))
}

func adminIndex(t *testing.T, root string) map[string]newsItem {
	t.Helper()
	return readIndex(t, filepath.Join(root, "news_private", "index.json"))
}

// getArticle calls the Get handler and returns the decoded answer.
func getArticle(t *testing.T, h *Handlers, slug string) (int, map[string]any) {
	t.Helper()
	w := httptest.NewRecorder()
	h.Get(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet,
		"http://example.com/admin/news/get?scope=launcher&slug="+url.QueryEscape(slug), nil))
	var out map[string]any
	_ = json.Unmarshal(w.Body.Bytes(), &out)
	return w.Code, out
}

// ===== Slugs =====

// Slugs are generated from the article headline in the admin UI, so a Russian
// headline produces a Cyrillic slug — that is the normal case for this project,
// not an edge case. Every handler builds a file path out of it, so if one of
// them tightened its validation to ASCII the editor would still save the
// article and then fail to open, publish or delete it.
func TestCyrillicSlugWorksInEveryHandler(t *testing.T) {
	h, root := newHandlers(t)
	const slug = "обновление-лаунчера_2.0"

	saveArticle(t, h, slug, "# Обновление\n\nЧто нового", false)

	code, got := getArticle(t, h, slug)
	if code != http.StatusOK {
		t.Fatalf("get: %d", code)
	}
	if !strings.Contains(markdownOf(t, got), "Обновление") {
		t.Fatalf("markdown lost: %v", got)
	}

	w := httptest.NewRecorder()
	h.Publish(w, urlencodedForm(t, "http://example.com/admin/news/publish", url.Values{
		"scope": {"launcher"}, "slug": {slug}, "published": {"true"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("publish: %d %s", w.Code, w.Body.String())
	}
	if _, ok := publicIndex(t, root)[slug]; !ok {
		t.Fatalf("the published Cyrillic slug is missing from the launcher index")
	}
	if _, err := os.Stat(filepath.Join(root, "news", slug+".md")); err != nil {
		t.Fatalf("the markdown is not in the served tree: %v", err)
	}

	w = httptest.NewRecorder()
	h.Delete(w, httptest.NewRequestWithContext(t.Context(), http.MethodPost,
		"http://example.com/admin/news/delete?scope=launcher&slug="+url.QueryEscape(slug), nil))
	if w.Code != http.StatusOK {
		t.Fatalf("delete: %d %s", w.Code, w.Body.String())
	}
	if _, ok := publicIndex(t, root)[slug]; ok {
		t.Fatal("the deleted article is still in the launcher index")
	}
}

// A Cyrillic slug carrying a traversal segment must still be refused. Allowing
// letters of any script must not become "allow anything that is not ASCII
// punctuation": "../" written around Cyrillic is the same escape.
func TestCyrillicSlugWithTraversalIsRejected(t *testing.T) {
	h, root := newHandlers(t)
	outside := filepath.Join(root, "секрет.md")
	if err := os.WriteFile(outside, []byte("TOP-SECRET"), 0o600); err != nil {
		t.Fatal(err)
	}
	for _, slug := range []string{"../секрет", "..\\секрет", "новости/секрет", "..новость", ".новость"} {
		w := httptest.NewRecorder()
		h.Get(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet,
			"http://example.com/admin/news/get?scope=launcher&slug="+url.QueryEscape(slug), nil))
		if w.Code != http.StatusBadRequest {
			t.Errorf("slug %q: got %d, want 400", slug, w.Code)
		}
		if strings.Contains(w.Body.String(), "TOP-SECRET") {
			t.Errorf("slug %q leaked a file from outside the news tree", slug)
		}
	}
	if _, err := os.Stat(outside); err != nil {
		t.Fatalf("the file outside the news tree was touched: %v", err)
	}
}

// ===== Index integrity =====

// index.json is rewritten by the admin while the public API is reading it. A
// save that gets rejected must not touch it at all: the launcher polls this file
// and a partial or emptied index shows every user an empty news feed.
func TestRejectedSaveLeavesTheIndexIntact(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "live", "# Live\n\nbody", true)

	pubPath := filepath.Join(root, "news", "index.json")
	before := readFile(t, pubPath)

	rejected := []map[string]string{
		{"scope": "launcher", "slug": "../pwned", "markdown": "# x"},
		{"scope": "launcher", "slug": "", "markdown": "# x"},
		{"scope": "game", "gameId": "../..", "slug": "ok", "markdown": "# x"},
		{"scope": "nonsense", "slug": "ok", "markdown": "# x"},
	}
	for _, fields := range rejected {
		w := httptest.NewRecorder()
		h.Save(w, multipartForm(t, "http://example.com/admin/news/save", fields))
		if w.Code != http.StatusBadRequest {
			t.Errorf("%v: got %d, want 400", fields, w.Code)
		}
	}

	// #nosec G304 -- pubPath is inside the t.TempDir() this test created.
	after, err := os.ReadFile(pubPath)
	if err != nil {
		t.Fatalf("the public index disappeared after rejected saves: %v", err)
	}
	if string(before) != string(after) {
		t.Fatalf("a rejected save rewrote the public index:\nbefore %s\nafter  %s", before, after)
	}
	if _, ok := publicIndex(t, root)["live"]; !ok {
		t.Fatal("the published article vanished from the index")
	}
}

// The atomic writer must not leave its scratch files inside the served tree:
// nginx hands out content/news wholesale, so a stray .index.json.tmp-* is a
// public URL, and one that is often a half-written document.
func TestNoTempFilesRemainInTheServedTree(t *testing.T) {
	h, root := newHandlers(t)
	for _, s := range []string{"a", "b", "c"} {
		saveArticle(t, h, s, "# "+s, true)
	}
	entries, err := os.ReadDir(filepath.Join(root, "news"))
	if err != nil {
		t.Fatal(err)
	}
	for _, e := range entries {
		if strings.Contains(e.Name(), ".tmp-") {
			t.Errorf("scratch file left in the public tree: %s", e.Name())
		}
	}
}

// Saving an article without touching the published checkbox must keep the flag
// it already had. If an absent form field read as "false", every edit of a live
// article would silently take it offline.
func TestSaveWithoutPublishedFieldKeepsTheFlag(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "live", "# v1", true)

	w := httptest.NewRecorder()
	h.Save(w, multipartForm(t, "http://example.com/admin/news/save", map[string]string{
		"scope": "launcher", "slug": "live", "markdown": "# v2",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("save: %d %s", w.Code, w.Body.String())
	}
	if !publicIndex(t, root)["live"].Published {
		t.Fatal("an edit without the published field unpublished a live article")
	}
	if _, err := os.Stat(filepath.Join(root, "news", "live.md")); err != nil {
		t.Fatalf("the article left the served tree: %v", err)
	}
}

// A stale copy of the same slug in both trees must resolve, not error out.
// That is the state an interrupted publish leaves behind, and the move has to
// replace the file already sitting at the destination — the body the published
// flag points at is the one that survives, and the served tree ends up clean.
func TestRebuildResolvesAnArticlePresentInBothTrees(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "story", "# public copy", true)

	// Simulate the leftover: an older draft body still sitting in the private
	// tree while the article is published.
	priv := filepath.Join(root, "news_private")
	if err := os.MkdirAll(priv, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(priv, "story.md"), []byte("# stale draft copy"), 0o600); err != nil {
		t.Fatal(err)
	}

	// Unpublish: the public copy must move over the stale private one.
	w := httptest.NewRecorder()
	h.Publish(w, urlencodedForm(t, "http://example.com/admin/news/publish", url.Values{
		"scope": {"launcher"}, "slug": {"story"}, "published": {"false"},
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("publish=false: %d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "news", "story.md")); err == nil {
		t.Fatal("the unpublished article is still in the served tree")
	}
	// #nosec G304 -- priv is inside the t.TempDir() this test created.
	b, err := os.ReadFile(filepath.Join(priv, "story.md"))
	if err != nil {
		t.Fatalf("the article was lost instead of moved: %v", err)
	}
	if string(b) != "# public copy" {
		t.Fatalf("the stale draft won over the live body: %q", string(b))
	}
}

// ===== Markdown bodies =====

// An empty article must not produce an index the launcher cannot parse. The
// editor allows saving a stub (title first, text later), and an unparseable
// index.json blanks the whole news feed, not just this one card.
func TestEmptyArticleStillProducesAValidIndex(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "stub", "", true)

	it, ok := publicIndex(t, root)["stub"]
	if !ok {
		t.Fatal("the empty article is missing from the index")
	}
	if it.Title != "" || it.Summary != "" {
		t.Errorf("an empty body produced content out of nowhere: %+v", it)
	}
	if it.CreatedAt == "" {
		t.Error("no createdAt: the launcher sorts by it, so an empty value scrambles the order")
	}
}

// An article with no H1 and no image has no title and no cover. The rebuild
// must fall back cleanly instead of promoting the first line into a heading or
// leaving the fields absent from the JSON.
func TestArticleWithoutMetadataFallsBackCleanly(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "plain", "просто текст без заголовка\n\nвторой абзац", true)

	it := publicIndex(t, root)["plain"]
	if it.Title != "" {
		t.Errorf("a title was invented: %q", it.Title)
	}
	if it.Summary != "просто текст без заголовка" {
		t.Errorf("summary = %q, want the first paragraph", it.Summary)
	}
	if it.CoverURL != "" {
		t.Errorf("a cover was invented: %q", it.CoverURL)
	}
}

// A long article must survive the round trip byte for byte. The editor's save
// is a multipart POST, and a body silently truncated at a parse limit would
// destroy the admin's work with a 200 OK.
func TestVeryLongArticleRoundTrips(t *testing.T) {
	h, root := newHandlers(t)
	body := "# Заголовок\n\n" + strings.Repeat("Длинный абзац текста. ", 40000)
	saveArticle(t, h, "long", body, true)

	code, got := getArticle(t, h, "long")
	if code != http.StatusOK {
		t.Fatalf("get: %d", code)
	}
	if md := markdownOf(t, got); md != body {
		t.Fatalf("the article was altered: stored %d bytes, got %d", len(body), len(md))
	}
	if _, ok := publicIndex(t, root)["long"]; !ok {
		t.Fatal("the long article is missing from the index")
	}
}

// The title and the summary are the two lines of every card the launcher
// draws. An article whose headline or opening paragraph is dropped here shows
// up as a blank card in the client even though the article itself is fine.
func TestExtractMetaTitleAndSummary(t *testing.T) {
	cases := []struct {
		name           string
		md             string
		title, summary string
	}{
		{"empty", "", "", ""},
		{"whitespace only", "\n\n   \n", "", ""},
		{"title only", "# Заголовок", "Заголовок", ""},
		{"title and paragraph", "# Заголовок\n\nПервый абзац\n\nВторой", "Заголовок", "Первый абзац"},
		{"no title", "просто текст", "", "просто текст"},
		// Windows editors and pasted content bring \r along; a summary ending in
		// a stray carriage return renders as a broken line in the client.
		{"crlf line endings", "# T\r\n\r\nabc\r\n", "T", "abc"},
		// Only the first H1 is the title. A further heading is still a heading,
		// not body text, so it must not become the card's summary line.
		{"second h1 is not a summary", "# first\n\n# second", "first", ""},
		// A multi-line paragraph is one summary, not just its first line.
		{"wrapped paragraph", "# T\n\nline one\nline two\n\nnext", "T", "line one\nline two"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			title, summary, _ := ExtractMeta(c.md)
			if title != c.title || summary != c.summary {
				t.Fatalf("got (%q, %q), want (%q, %q)", title, summary, c.title, c.summary)
			}
		})
	}
}

// Nearly every article here opens with its cover picture on the first line,
// often followed by a subheading. The summary is drawn as plain text on the
// card — in the launcher's news list and on the landing page — so taking the
// opening block verbatim showed the reader "![c](pic.png)" where the first
// sentence of the article was supposed to be.
func TestSummarySkipsMarkupAndTakesTheFirstRealText(t *testing.T) {
	cases := []struct {
		name, md, summary string
	}{
		{"cover on the first line", "![c](pic.png)\n\n# Заголовок\n\nПервый абзац", "Первый абзац"},
		{"cover then a subheading", "![c](pic.png)\n\n# T\n\n## Что нового\n\nТекст", "Текст"},
		{"cover glued to the heading", "![c](pic.png)\n# T\nТекст", "Текст"},
		{"horizontal rule first", "# T\n\n---\n\nТекст", "Текст"},
		{"code fence first", "# T\n\n```\ngo build\n```\n\nТекст", "Текст"},
		// A bullet list is perfectly readable once the bullets are gone; patch
		// notes are written that way and would otherwise get no summary at all.
		{"bullet list", "# T\n\n- первый пункт\n- второй пункт", "первый пункт\nвторой пункт"},
		{"numbered list", "# T\n\n1. первый\n2. второй", "первый\nвторой"},
		{"quote", "# T\n\n> цитата", "цитата"},
		{"link keeps its label", "# T\n\nСкачайте [лаунчер](https://x.dev) сейчас", "Скачайте лаунчер сейчас"},
		{"emphasis is dropped", "# T\n\n**Важно:** мы *обновились*", "Важно: мы обновились"},
		// An article that is nothing but pictures has no summary. An empty card
		// line is honest; a line of markdown is not.
		{"pictures only", "![a](1.png)\n\n![b](2.png)", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if _, summary, _ := ExtractMeta(c.md); summary != c.summary {
				t.Fatalf("summary = %q, want %q", summary, c.summary)
			}
		})
	}
}

// The same thing end to end: what the launcher actually downloads is
// index.json, and that is where the raw markdown used to land.
func TestPublishedIndexCarriesNoMarkdownInTheSummary(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "cover-first", "![c](pic.png)\n\n# Обновление\n\nМы починили загрузку.", true)

	it := publicIndex(t, root)["cover-first"]
	if it.Summary != "Мы починили загрузку." {
		t.Fatalf("summary = %q, want the first sentence of the article", it.Summary)
	}
	if it.CoverURL != "" {
		t.Errorf("the cover comes from the metadata, not from the body: %q", it.CoverURL)
	}
}

// index.json is fetched by every launcher on every start. An article whose
// opening paragraph is a wall of text used to be shipped in it in full, so one
// long post inflated the file every client downloads. The cut has to happen on
// a rune boundary: the articles are in Russian and a byte-sized cut splits a
// two-byte character into a replacement glyph on the card.
func TestSummaryIsBoundedAndCutOnAWordAndRuneBoundary(t *testing.T) {
	h, root := newHandlers(t)
	para := strings.Repeat("Очень длинный абзац без единого разрыва строки. ", 20000)
	saveArticle(t, h, "long", "# Заголовок\n\n"+para, true)

	it := publicIndex(t, root)["long"]
	if n := utf8.RuneCountInString(it.Summary); n > 300 {
		t.Fatalf("summary is %d runes long; index.json ships this to every client", n)
	}
	if !utf8.ValidString(it.Summary) || strings.ContainsRune(it.Summary, '�') {
		t.Fatalf("the cut broke a multi-byte character: %q", it.Summary)
	}
	if !strings.HasSuffix(it.Summary, "…") {
		t.Fatalf("a shortened summary must say so: %q", it.Summary)
	}
	// The last word must be whole: the text before the ellipsis is a prefix of
	// the paragraph that ends exactly where a space does.
	head := strings.TrimSuffix(it.Summary, "…")
	if !strings.HasPrefix(para, head) || !strings.HasPrefix(para[len(head):], " ") {
		t.Fatalf("the summary was cut mid-word: %q", it.Summary)
	}
	// Only the index is shortened; the article itself is untouched.
	code, got := getArticle(t, h, "long")
	if code != http.StatusOK || !strings.HasSuffix(markdownOf(t, got), para) {
		t.Fatalf("the stored article was truncated too (%d)", code)
	}
}

// A summary that fits must stay exactly as written — no stray ellipsis on the
// short cards, which are the majority.
func TestShortSummaryIsLeftAlone(t *testing.T) {
	_, summary, _ := ExtractMeta("# T\n\nКороткий анонс.")
	if summary != "Короткий анонс." {
		t.Fatalf("summary = %q, want it verbatim", summary)
	}
}

// The cover is the first inline image, turned into the /assets/ path the
// launcher and the landing page resolve. Getting the normalisation wrong points
// every card at a URL that 404s.
func TestExtractMetaCoverNormalisation(t *testing.T) {
	cases := []struct {
		name, md, cover string
	}{
		{"no image", "# T\n\ntext", ""},
		{"bare filename", "# T\n\n![c](pic.png)", "/assets/pic.png"},
		{"assets-prefixed", "# T\n\n![c](assets/sub/a.png)", "/assets/sub/a.png"},
		{"dot-slash", "# T\n\n![c](./a.png)", "/assets/a.png"},
		{"already rooted", "# T\n\n![c](/img/a.png)", "/img/a.png"},
		{"absolute http url", "# T\n\n![c](https://cdn.example/a.png)", "https://cdn.example/a.png"},
		{"image before the title", "![c](pic.png)\n\n# T", "/assets/pic.png"},
		{"first image wins", "# T\n\n![a](one.png)\n\n![b](two.png)", "/assets/one.png"},
		// Half-typed markdown is what the editor sees on most keystrokes; it
		// must yield no cover rather than a garbage URL.
		{"unterminated", "# T\n\n![c](broken", ""},
		{"empty target", "# T\n\n![c]()", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if _, _, cover := ExtractMeta(c.md); cover != c.cover {
				t.Fatalf("cover = %q, want %q", cover, c.cover)
			}
		})
	}
}

// ===== Covers and assets =====

// Uploading a cover for a slug records it on the article. The launcher reads
// coverUrl straight out of index.json, so a cover that only lands on disk is a
// card with a missing image.
func TestUploadCoverAttachesTheImageToTheArticle(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "story", "# story", true)

	w := httptest.NewRecorder()
	h.UploadCover(w, coverUpload(t, "cover.png", map[string]string{
		"scope": "launcher", "slug": "story",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("uploadCover: %d %s", w.Code, w.Body.String())
	}
	var resp map[string]string
	if err := json.Unmarshal(w.Body.Bytes(), &resp); err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(resp["coverUrl"], "/assets/") {
		t.Fatalf("coverUrl = %q, the launcher resolves it against /assets/", resp["coverUrl"])
	}
	if got := publicIndex(t, root)["story"].CoverURL; got != resp["coverUrl"] {
		t.Errorf("index coverUrl = %q, want %q", got, resp["coverUrl"])
	}
	if _, m := getArticle(t, h, "story"); m["coverUrl"] != resp["coverUrl"] {
		t.Errorf("the editor would not show the new cover: %v", m)
	}
}

// A traversal slug on the cover upload must be refused before it can write a
// metadata entry — the image itself is stored under a sanitised name, but the
// slug is what addresses news_meta.json.
func TestUploadCoverRejectsTraversalSlug(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "story", "# story", true)

	for _, slug := range traversalSlugs {
		w := httptest.NewRecorder()
		h.UploadCover(w, coverUpload(t, "cover.png", map[string]string{
			"scope": "launcher", "slug": slug,
		}))
		if w.Code != http.StatusBadRequest {
			t.Errorf("slug %q: got %d, want 400", slug, w.Code)
		}
	}
	if publicIndex(t, root)["story"].CoverURL != "" {
		t.Error("a rejected upload still changed an article's cover")
	}
	// The rejection also has to come before the file hits the disk. The name is
	// sanitised, so nothing escapes the gallery, but every refused upload used to
	// leave one more orphan in it — pictures nobody can attribute to an article,
	// piling up in the picker every editor browses.
	entries, err := os.ReadDir(filepath.Join(root, "news", "assets"))
	if err == nil && len(entries) > 0 {
		got := make([]string, 0, len(entries))
		for _, e := range entries {
			got = append(got, e.Name())
		}
		t.Errorf("rejected cover uploads left files in the gallery: %v", got)
	}
}

// Deleting an article must not delete the images: content/news/assets is one
// shared gallery, and the same picture is routinely used by several articles
// and by the landing page. Removing it would break every other reference.
func TestDeleteKeepsTheSharedAssetButDropsTheMetaEntry(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "story", "# story", true)

	w := httptest.NewRecorder()
	h.UploadCover(w, coverUpload(t, "shared.png", map[string]string{
		"scope": "launcher", "slug": "story",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("uploadCover: %d %s", w.Code, w.Body.String())
	}
	asset := filepath.Join(root, "news", "assets", "shared.png")
	if _, err := os.Stat(asset); err != nil {
		t.Fatalf("the cover was not stored: %v", err)
	}

	w = httptest.NewRecorder()
	h.Delete(w, httptest.NewRequestWithContext(t.Context(), http.MethodPost,
		"http://example.com/admin/news/delete?scope=launcher&slug=story", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("delete: %d %s", w.Code, w.Body.String())
	}

	if _, err := os.Stat(asset); err != nil {
		t.Errorf("deleting an article removed a shared gallery image: %v", err)
	}
	// The dangling coverUrl must be gone from both the metadata and the index,
	// otherwise the next rebuild resurrects the deleted card.
	b := readFile(t, filepath.Join(root, "news_private", "news_meta.json"))
	if strings.Contains(string(b), "story") {
		t.Errorf("news_meta.json still references the deleted article: %s", b)
	}
	if _, ok := adminIndex(t, root)["story"]; ok {
		t.Error("the admin index still lists the deleted article")
	}
}

// ===== Error paths =====

// A slug that does not exist must read as "not found", not as an empty article
// the editor would then happily save over the real one.
func TestGetMissingArticleIsNotFound(t *testing.T) {
	h, _ := newHandlers(t)
	if code, _ := getArticle(t, h, "no-such-article"); code != http.StatusNotFound {
		t.Fatalf("got %d, want 404", code)
	}
	w := httptest.NewRecorder()
	h.Get(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://example.com/admin/news/get?scope=launcher", nil))
	if w.Code != http.StatusBadRequest {
		t.Fatalf("a missing slug answered %d, want 400", w.Code)
	}
}

// Deleting something that is not there must not report success: the admin UI
// removes the row from its list on 200, so a false OK hides an article that is
// still live for every user.
//
// It must be a 404 and not a 500 either. The panel treats 5xx as "the backend
// is down" — it retries and tells the admin the server is broken, when the only
// thing that happened is that the row they clicked was already gone.
func TestDeleteMissingArticleIsNotFound(t *testing.T) {
	h, _ := newHandlers(t)
	w := httptest.NewRecorder()
	h.Delete(w, httptest.NewRequestWithContext(t.Context(), http.MethodPost,
		"http://example.com/admin/news/delete?scope=launcher&slug=ghost", nil))
	if w.Code != http.StatusNotFound {
		t.Fatalf("deleting a missing article answered %d, want 404", w.Code)
	}
}

// Publishing a slug that has no markdown file must fail and change nothing.
// It used to answer 200 and put an entry into news_meta.json that no article
// corresponds to: RebuildIndex only walks the .md files, so the entry never
// showed up anywhere and could not be cleared from the panel — it just sat
// there, and would have flipped a real article to "published" the moment
// somebody later created an article with that slug.
func TestPublishingAMissingArticleIsNotFoundAndWritesNoMetadata(t *testing.T) {
	h, root := newHandlers(t)
	saveArticle(t, h, "real", "# real", false)

	w := httptest.NewRecorder()
	h.Publish(w, urlencodedForm(t, "http://example.com/admin/news/publish", url.Values{
		"scope": {"launcher"}, "slug": {"ghost"}, "published": {"true"},
	}))
	if w.Code != http.StatusNotFound {
		t.Fatalf("got %d, want 404: %s", w.Code, w.Body.String())
	}

	b := readFile(t, filepath.Join(root, "news_private", "news_meta.json"))
	if strings.Contains(string(b), "ghost") {
		t.Fatalf("a phantom entry was written into news_meta.json: %s", b)
	}
	if _, ok := adminIndex(t, root)["ghost"]; ok {
		t.Error("the admin index lists an article that has no file")
	}
	// The real article was not disturbed on the way.
	if adminIndex(t, root)["real"].Published {
		t.Error("the rejected publish leaked onto another article")
	}
}

// List on a scope that has never been written must produce an empty index
// rather than a 404: a fresh game gets its news tab from this call, and an
// error there looks like a broken admin panel.
func TestListBuildsAnEmptyIndexForAFreshScope(t *testing.T) {
	h, _ := newHandlers(t)
	w := httptest.NewRecorder()
	h.List(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet,
		"http://example.com/admin/news/list?scope=game&gameId=brandnew", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("list: %d %s", w.Code, w.Body.String())
	}
	var idx struct {
		Items []newsItem `json:"items"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &idx); err != nil {
		t.Fatalf("list is not valid JSON: %v (%s)", err, w.Body.String())
	}
	if len(idx.Items) != 0 {
		t.Fatalf("a fresh scope already has articles: %+v", idx.Items)
	}
}

// The write endpoints must refuse GET. Without that, a link or an <img> tag
// pointing at /admin/api/news/delete?slug=… deletes an article on click.
func TestNewsWriteEndpointsRejectGet(t *testing.T) {
	h, _ := newHandlers(t)
	for name, handler := range map[string]http.HandlerFunc{
		"save":        h.Save,
		"delete":      h.Delete,
		"publish":     h.Publish,
		"rebuild":     h.Rebuild,
		"preview":     h.Preview,
		"uploadCover": h.UploadCover,
	} {
		w := httptest.NewRecorder()
		handler(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://example.com/admin/news/"+name+"?scope=launcher", nil))
		if w.Code != http.StatusMethodNotAllowed {
			t.Errorf("%s answered GET with %d, want 405", name, w.Code)
		}
	}
}

// A malformed urlencoded body must be a 400 rather than a publish decided by
// whatever ParseForm managed to salvage.
func TestPublishRejectsMalformedForm(t *testing.T) {
	h, _ := newHandlers(t)
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://example.com/admin/news/publish",
		strings.NewReader("scope=launcher&slug=%zz&published=true"))
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	w := httptest.NewRecorder()
	h.Publish(w, req)
	if w.Code != http.StatusBadRequest {
		t.Fatalf("got %d, want 400", w.Code)
	}
}

// Delete addresses the article by query string, so the scope guard has to hold
// there too — a bad gameId must not resolve to another game's news directory.
func TestDeleteRejectsBadScope(t *testing.T) {
	h, _ := newHandlers(t)
	for _, q := range []string{
		"scope=game&slug=x",
		"scope=game&gameId=../..&slug=x",
		"scope=nonsense&slug=x",
		"scope=launcher&slug=%20%20",
	} {
		w := httptest.NewRecorder()
		h.Delete(w, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://example.com/admin/news/delete?"+q, nil))
		if w.Code != http.StatusBadRequest {
			t.Errorf("%s: got %d, want 400", q, w.Code)
		}
	}
}

// A malformed multipart body must be a 400, not a panic or a partial write.
func TestSaveRejectsMalformedBody(t *testing.T) {
	h, _ := newHandlers(t)
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://example.com/admin/news/save",
		strings.NewReader("this is not multipart"))
	req.Header.Set("Content-Type", "multipart/form-data; boundary=nope")
	w := httptest.NewRecorder()
	h.Save(w, req)
	if w.Code != http.StatusBadRequest {
		t.Fatalf("got %d, want 400", w.Code)
	}
}

// ===== Preview =====

// Preview renders the body with the server's own converter — the launcher
// fetches the raw .md and renders it itself — so a body that can break out of
// an attribute here is a stored XSS in the admin panel.
func TestPreviewRendersTheCardAndEscapesPayloads(t *testing.T) {
	h, _ := newHandlers(t)
	w := httptest.NewRecorder()
	h.Preview(w, multipartForm(t, "http://example.com/admin/news/preview", map[string]string{
		"markdown": "![cover](pic.png)\n\n# Заголовок\n\nПервый абзац\n",
	}))
	if w.Code != http.StatusOK {
		t.Fatalf("preview: %d %s", w.Code, w.Body.String())
	}
	var out map[string]string
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out["listHtml"], `src="/assets/pic.png"`) {
		t.Errorf("the card lost its cover: %s", out["listHtml"])
	}
	if !strings.Contains(out["listHtml"], "Заголовок") {
		t.Errorf("the card lost its title: %s", out["listHtml"])
	}
	if !strings.Contains(out["contentHtml"], "<h1>") {
		t.Errorf("the article body was not rendered: %s", out["contentHtml"])
	}

	w = httptest.NewRecorder()
	h.Preview(w, multipartForm(t, "http://example.com/admin/news/preview", map[string]string{
		"markdown": `![x" onerror="alert(1)](/p.png)` + "\n\n# t\n",
	}))
	body := w.Body.String()
	if strings.Contains(body, `onerror=\"`) || strings.Contains(body, `onerror="`) {
		t.Errorf("an event handler survived into the preview card: %s", body)
	}
}

// An empty preview must answer with empty HTML rather than fail: the editor
// calls it on every keystroke, including the first one.
func TestPreviewOfAnEmptyBody(t *testing.T) {
	h, _ := newHandlers(t)
	w := httptest.NewRecorder()
	h.Preview(w, multipartForm(t, "http://example.com/admin/news/preview", map[string]string{"markdown": ""}))
	if w.Code != http.StatusOK {
		t.Fatalf("preview: %d %s", w.Code, w.Body.String())
	}
	var out map[string]string
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	if strings.TrimSpace(out["contentHtml"]) != "" {
		t.Errorf("an empty body rendered content: %q", out["contentHtml"])
	}
}

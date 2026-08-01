package main

import (
	"bytes"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// withTempContentRoot points contentRoot at a throwaway directory for one test.
func withTempContentRoot(t *testing.T) string {
	t.Helper()
	old := contentRoot
	dir := t.TempDir()
	contentRoot = dir
	t.Cleanup(func() { contentRoot = old })
	return dir
}

// multipartForm builds a multipart/form-data POST request out of plain fields.
func multipartForm(t *testing.T, rawURL string, fields map[string]string) *http.Request {
	t.Helper()
	var buf bytes.Buffer
	mw := multipart.NewWriter(&buf)
	for k, v := range fields {
		if err := mw.WriteField(k, v); err != nil {
			t.Fatalf("write field %s: %v", k, err)
		}
	}
	if err := mw.Close(); err != nil {
		t.Fatalf("close multipart: %v", err)
	}
	req := httptest.NewRequest(http.MethodPost, rawURL, &buf)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// urlencodedForm builds an application/x-www-form-urlencoded POST request.
func urlencodedForm(t *testing.T, rawURL string, values url.Values) *http.Request {
	t.Helper()
	req := httptest.NewRequest(http.MethodPost, rawURL, strings.NewReader(values.Encode()))
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	return req
}

// traversalSlugs are slugs that must never be turned into a filesystem path.
var traversalSlugs = []string{
	"../../../pwned",
	"..\\..\\pwned",
	"../secret",
	"a/../../b",
	"/etc/passwd",
	"..",
	".hidden",
	"sub/dir",
}

// traversalGameIDs are gameId values that must never reach newsBase's path join.
var traversalGameIDs = []string{
	"../../..",
	"..",
	"../other",
	"a/b",
	"a\\b",
	"....//",
	"games/../../..",
}

func TestIsSafeNewsSlug(t *testing.T) {
	for _, s := range traversalSlugs {
		if isSafeNewsSlug(s) {
			t.Errorf("slug %q must be rejected", s)
		}
	}
	if isSafeNewsSlug("") {
		t.Error("empty slug must be rejected")
	}
	// legitimate slugs produced by the admin UI, including Cyrillic ones
	for _, s := range []string{"patch-1", "release_2024", "v1.2.3", "новость-1"} {
		if !isSafeNewsSlug(s) {
			t.Errorf("slug %q must be accepted", s)
		}
	}
}

func TestNewsBaseRejectsTraversalGameID(t *testing.T) {
	withTempContentRoot(t)
	for _, gid := range traversalGameIDs {
		if p, err := newsBase("game", gid); err == nil {
			t.Errorf("gameId %q accepted, resolved to %q", gid, p)
		}
	}
	if _, err := newsBase("game", "mygame"); err != nil {
		t.Errorf("valid gameId rejected: %v", err)
	}
}

func TestNewsSlugPathRejectsTraversal(t *testing.T) {
	root := withTempContentRoot(t)
	base := filepath.Join(root, "news")
	for _, s := range traversalSlugs {
		if p, err := newsSlugPath(base, s); err == nil {
			t.Errorf("slug %q accepted, resolved to %q", s, p)
		}
	}
}

// handleNewsGet must not serve files from outside the news directory.
func TestHandleNewsGetRejectsTraversal(t *testing.T) {
	root := withTempContentRoot(t)
	secret := filepath.Join(root, "secret.md")
	if err := os.WriteFile(secret, []byte("TOP-SECRET"), 0o644); err != nil {
		t.Fatal(err)
	}
	for _, slug := range traversalSlugs {
		req := httptest.NewRequest(http.MethodGet,
			"http://example.com/admin/news/get?scope=launcher&slug="+url.QueryEscape(slug), nil)
		w := httptest.NewRecorder()
		handleNewsGet(w, req)
		if w.Code != http.StatusBadRequest {
			t.Errorf("slug %q: expected 400, got %d", slug, w.Code)
		}
		if strings.Contains(w.Body.String(), "TOP-SECRET") {
			t.Errorf("slug %q leaked file contents", slug)
		}
	}
}

// handleNewsSave must not write markdown outside the news directory.
func TestHandleNewsSaveRejectsTraversal(t *testing.T) {
	root := withTempContentRoot(t)
	for _, slug := range traversalSlugs {
		req := multipartForm(t, "http://example.com/admin/news/save", map[string]string{
			"scope":    "launcher",
			"slug":     slug,
			"markdown": "# pwned",
		})
		w := httptest.NewRecorder()
		handleNewsSave(w, req)
		if w.Code != http.StatusBadRequest {
			t.Errorf("slug %q: expected 400, got %d", slug, w.Code)
		}
	}
	if _, err := os.Stat(filepath.Join(root, "pwned.md")); err == nil {
		t.Error("file was written outside the news directory")
	}
	// gameId traversal is rejected as well
	for _, gid := range traversalGameIDs {
		req := multipartForm(t, "http://example.com/admin/news/save", map[string]string{
			"scope":    "game",
			"gameId":   gid,
			"slug":     "ok-slug",
			"markdown": "# pwned",
		})
		w := httptest.NewRecorder()
		handleNewsSave(w, req)
		if w.Code != http.StatusBadRequest {
			t.Errorf("gameId %q: expected 400, got %d", gid, w.Code)
		}
	}
}

// handleNewsDelete must not remove files outside the news directory.
func TestHandleNewsDeleteRejectsTraversal(t *testing.T) {
	root := withTempContentRoot(t)
	victim := filepath.Join(root, "victim.md")
	if err := os.WriteFile(victim, []byte("keep me"), 0o644); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/news/delete?scope=launcher&slug="+url.QueryEscape("../victim"), nil)
	w := httptest.NewRecorder()
	handleNewsDelete(w, req)
	if w.Code != http.StatusBadRequest {
		t.Fatalf("expected 400, got %d", w.Code)
	}
	if _, err := os.Stat(victim); err != nil {
		t.Fatalf("file outside news dir was deleted: %v", err)
	}
}

func TestHandleNewsPublishRejectsTraversal(t *testing.T) {
	withTempContentRoot(t)
	for _, slug := range traversalSlugs {
		req := urlencodedForm(t, "http://example.com/admin/news/publish", url.Values{
			"scope":     {"launcher"},
			"slug":      {slug},
			"published": {"true"},
		})
		w := httptest.NewRecorder()
		handleNewsPublish(w, req)
		if w.Code != http.StatusBadRequest {
			t.Errorf("slug %q: expected 400, got %d", slug, w.Code)
		}
	}
}

// scope/gameId-only endpoints must reject traversal in gameId too.
func TestNewsScopeHandlersRejectTraversalGameID(t *testing.T) {
	withTempContentRoot(t)
	cases := []struct {
		name   string
		h      http.HandlerFunc
		method string
		path   string
	}{
		{"newsList", handleNewsList, http.MethodGet, "/admin/news/list"},
		{"newsRebuild", handleNewsRebuild, http.MethodPost, "/admin/news/rebuild"},
	}
	for _, tc := range cases {
		for _, gid := range traversalGameIDs {
			req := httptest.NewRequest(tc.method,
				"http://example.com"+tc.path+"?scope=game&gameId="+url.QueryEscape(gid), nil)
			w := httptest.NewRecorder()
			tc.h(w, req)
			if w.Code != http.StatusBadRequest {
				t.Errorf("%s gameId %q: expected 400, got %d", tc.name, gid, w.Code)
			}
		}
	}
}

// Legitimate slugs must still round-trip through save -> get -> delete.
func TestNewsSaveGetDeleteRoundTrip(t *testing.T) {
	withTempContentRoot(t)
	const slug = "новость-1"
	save := multipartForm(t, "http://example.com/admin/news/save", map[string]string{
		"scope":    "launcher",
		"slug":     slug,
		"markdown": "# Hello",
	})
	w := httptest.NewRecorder()
	handleNewsSave(w, save)
	if w.Code != http.StatusOK {
		t.Fatalf("save: expected 200, got %d (%s)", w.Code, w.Body.String())
	}

	get := httptest.NewRequest(http.MethodGet,
		"http://example.com/admin/news/get?scope=launcher&slug="+url.QueryEscape(slug), nil)
	w = httptest.NewRecorder()
	handleNewsGet(w, get)
	if w.Code != http.StatusOK {
		t.Fatalf("get: expected 200, got %d (%s)", w.Code, w.Body.String())
	}
	if !strings.Contains(w.Body.String(), "Hello") {
		t.Fatalf("get: markdown not returned: %s", w.Body.String())
	}

	del := httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/news/delete?scope=launcher&slug="+url.QueryEscape(slug), nil)
	w = httptest.NewRecorder()
	handleNewsDelete(w, del)
	if w.Code != http.StatusOK {
		t.Fatalf("delete: expected 200, got %d (%s)", w.Code, w.Body.String())
	}
}

// promoteVersionDir must replace an existing published directory (os.Rename
// alone cannot overwrite a directory).
func TestPromoteVersionDirReplacesExisting(t *testing.T) {
	root := withTempContentRoot(t)
	final := filepath.Join(root, "content", "game", "1.0.0")
	if err := os.MkdirAll(filepath.Join(final, "files"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(final, "files", "old.txt"), []byte("old"), 0o644); err != nil {
		t.Fatal(err)
	}
	stage, filesRoot, err := stageVersionDir("game", "1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	if stage == final {
		t.Fatal("staging dir must differ from the published dir")
	}
	if err := os.WriteFile(filepath.Join(filesRoot, "new.txt"), []byte("new"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := promoteVersionDir(stage, final); err != nil {
		t.Fatalf("promote: %v", err)
	}
	if _, err := os.Stat(filepath.Join(final, "files", "new.txt")); err != nil {
		t.Fatalf("new build not published: %v", err)
	}
	if _, err := os.Stat(filepath.Join(final, "files", "old.txt")); err == nil {
		t.Fatal("old build files survived the replacement")
	}
	if _, err := os.Stat(stage); err == nil {
		t.Fatal("staging dir still exists after promote")
	}
}

// Feedback rotation must bound the number of stored reports.
func TestPruneFeedbackItemsRotatesOldest(t *testing.T) {
	items := make([]FeedbackItem, feedbackMaxItems+50)
	for i := range items {
		items[i] = FeedbackItem{ID: genID(), Comment: "c"}
	}
	items[0].ID = "newest"
	out := pruneFeedbackItems(items)
	if len(out) != feedbackMaxItems {
		t.Fatalf("expected %d items, got %d", feedbackMaxItems, len(out))
	}
	if out[0].ID != "newest" {
		t.Fatal("newest report was dropped")
	}
}

func TestMutatingHandlersRejectGET(t *testing.T) {
	cases := []struct {
		name string
		h    http.HandlerFunc
		url  string
	}{
		{name: "activate", h: handleActivate, url: "http://example.com/admin/activate?gameId=launcher&version=1.0.0"},
		{name: "deleteVersion", h: handleDeleteVersion, url: "http://example.com/admin/deleteVersion?gameId=launcher&version=1.0.0"},
		{name: "upload", h: handleUpload, url: "http://example.com/admin/upload"},
		{name: "uploadStream", h: handleUploadStream, url: "http://example.com/admin/uploadStream"},

		{name: "feedbackDelete", h: handleFeedbackDelete, url: "http://example.com/admin/feedback/delete?id=1"},
		{name: "feedbackToggleImportant", h: handleFeedbackToggleImportant, url: "http://example.com/admin/feedback/toggleImportant?id=1"},
		{name: "feedbackMarkRead", h: handleFeedbackMarkRead, url: "http://example.com/admin/feedback/markRead?id=1"},
		{name: "feedbackMarkUnread", h: handleFeedbackMarkUnread, url: "http://example.com/admin/feedback/markUnread?id=1"},
		{name: "feedbackClear", h: handleFeedbackClear, url: "http://example.com/admin/feedback/clear"},

		{name: "gamesSave", h: handleGamesSave, url: "http://example.com/admin/games/save"},
		{name: "gameIconUpload", h: handleGameIconUpload, url: "http://example.com/admin/games/icon/upload?gameId=test"},

		{name: "newsRebuild", h: handleNewsRebuild, url: "http://example.com/admin/news/rebuild?scope=global"},
		{name: "newsSave", h: handleNewsSave, url: "http://example.com/admin/news/save"},
		{name: "newsDelete", h: handleNewsDelete, url: "http://example.com/admin/news/delete?scope=global&slug=test"},
		{name: "newsPublish", h: handleNewsPublish, url: "http://example.com/admin/news/publish"},
		{name: "newsPreview", h: handleNewsPreview, url: "http://example.com/admin/news/preview"},
		{name: "newsUploadCover", h: handleNewsUploadCover, url: "http://example.com/admin/news/uploadCover"},

		{name: "newsAssetsMkdir", h: handleNewsAssetsMkdir, url: "http://example.com/admin/news/assets/mkdir"},
		{name: "newsAssetsUpload", h: handleNewsAssetsUpload, url: "http://example.com/admin/news/assets/upload"},
		{name: "newsAssetsUploadByURL", h: handleNewsAssetsUploadByURL, url: "http://example.com/admin/news/assets/uploadByUrl"},
		{name: "newsAssetsDelete", h: handleNewsAssetsDelete, url: "http://example.com/admin/news/assets/delete"},
		{name: "newsAssetsRename", h: handleNewsAssetsRename, url: "http://example.com/admin/news/assets/rename"},
	}

	for _, tc := range cases {
		tc := tc
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequest(http.MethodGet, tc.url, nil)
			w := httptest.NewRecorder()
			tc.h(w, req)
			if w.Code != http.StatusMethodNotAllowed {
				t.Fatalf("expected %d, got %d", http.StatusMethodNotAllowed, w.Code)
			}
		})
	}
}

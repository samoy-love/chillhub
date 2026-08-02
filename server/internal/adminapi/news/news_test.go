package news

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

// newHandlers points a handler set at a throwaway content root for one test.
func newHandlers(t *testing.T) (*Handlers, string) {
	t.Helper()
	dir := t.TempDir()
	return New(dir), dir
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
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, rawURL, &buf)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// urlencodedForm builds an application/x-www-form-urlencoded POST request.
func urlencodedForm(t *testing.T, rawURL string, values url.Values) *http.Request {
	t.Helper()
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, rawURL, strings.NewReader(values.Encode()))
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

// traversalGameIDs are gameId values that must never reach Base's path join.
var traversalGameIDs = []string{
	"../../..",
	"..",
	"../other",
	"a/b",
	"a\\b",
	"....//",
	"games/../../..",
}

func TestNewsBaseRejectsTraversalGameID(t *testing.T) {
	h, _ := newHandlers(t)
	for _, gid := range traversalGameIDs {
		if p, err := h.Base("game", gid); err == nil {
			t.Errorf("gameId %q accepted, resolved to %q", gid, p)
		}
	}
	if _, err := h.Base("game", "mygame"); err != nil {
		t.Errorf("valid gameId rejected: %v", err)
	}
}

// Get must not serve files from outside the news directory.
func TestHandleNewsGetRejectsTraversal(t *testing.T) {
	h, root := newHandlers(t)
	secret := filepath.Join(root, "secret.md")
	if err := os.WriteFile(secret, []byte("TOP-SECRET"), 0o600); err != nil {
		t.Fatal(err)
	}
	for _, slug := range traversalSlugs {
		req := httptest.NewRequestWithContext(t.Context(), http.MethodGet,
			"http://example.com/admin/news/get?scope=launcher&slug="+url.QueryEscape(slug), nil)
		w := httptest.NewRecorder()
		h.Get(w, req)
		if w.Code != http.StatusBadRequest {
			t.Errorf("slug %q: expected 400, got %d", slug, w.Code)
		}
		if strings.Contains(w.Body.String(), "TOP-SECRET") {
			t.Errorf("slug %q leaked file contents", slug)
		}
	}
}

// Save must not write markdown outside the news directory.
func TestHandleNewsSaveRejectsTraversal(t *testing.T) {
	h, root := newHandlers(t)
	for _, slug := range traversalSlugs {
		req := multipartForm(t, "http://example.com/admin/news/save", map[string]string{
			"scope":    "launcher",
			"slug":     slug,
			"markdown": "# pwned",
		})
		w := httptest.NewRecorder()
		h.Save(w, req)
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
		h.Save(w, req)
		if w.Code != http.StatusBadRequest {
			t.Errorf("gameId %q: expected 400, got %d", gid, w.Code)
		}
	}
}

// Delete must not remove files outside the news directory.
func TestHandleNewsDeleteRejectsTraversal(t *testing.T) {
	h, root := newHandlers(t)
	victim := filepath.Join(root, "victim.md")
	if err := os.WriteFile(victim, []byte("keep me"), 0o600); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost,
		"http://example.com/admin/news/delete?scope=launcher&slug="+url.QueryEscape("../victim"), nil)
	w := httptest.NewRecorder()
	h.Delete(w, req)
	if w.Code != http.StatusBadRequest {
		t.Fatalf("expected 400, got %d", w.Code)
	}
	if _, err := os.Stat(victim); err != nil {
		t.Fatalf("file outside news dir was deleted: %v", err)
	}
}

func TestHandleNewsPublishRejectsTraversal(t *testing.T) {
	h, _ := newHandlers(t)
	for _, slug := range traversalSlugs {
		req := urlencodedForm(t, "http://example.com/admin/news/publish", url.Values{
			"scope":     {"launcher"},
			"slug":      {slug},
			"published": {"true"},
		})
		w := httptest.NewRecorder()
		h.Publish(w, req)
		if w.Code != http.StatusBadRequest {
			t.Errorf("slug %q: expected 400, got %d", slug, w.Code)
		}
	}
}

// scope/gameId-only endpoints must reject traversal in gameId too.
func TestNewsScopeHandlersRejectTraversalGameID(t *testing.T) {
	h, _ := newHandlers(t)
	cases := []struct {
		name   string
		h      http.HandlerFunc
		method string
		path   string
	}{
		{"newsList", h.List, http.MethodGet, "/admin/news/list"},
		{"newsRebuild", h.Rebuild, http.MethodPost, "/admin/news/rebuild"},
	}
	for _, tc := range cases {
		for _, gid := range traversalGameIDs {
			req := httptest.NewRequestWithContext(t.Context(), tc.method,
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
	h, _ := newHandlers(t)
	const slug = "новость-1"
	save := multipartForm(t, "http://example.com/admin/news/save", map[string]string{
		"scope":    "launcher",
		"slug":     slug,
		"markdown": "# Hello",
	})
	w := httptest.NewRecorder()
	h.Save(w, save)
	if w.Code != http.StatusOK {
		t.Fatalf("save: expected 200, got %d (%s)", w.Code, w.Body.String())
	}

	get := httptest.NewRequestWithContext(t.Context(), http.MethodGet,
		"http://example.com/admin/news/get?scope=launcher&slug="+url.QueryEscape(slug), nil)
	w = httptest.NewRecorder()
	h.Get(w, get)
	if w.Code != http.StatusOK {
		t.Fatalf("get: expected 200, got %d (%s)", w.Code, w.Body.String())
	}
	if !strings.Contains(w.Body.String(), "Hello") {
		t.Fatalf("get: markdown not returned: %s", w.Body.String())
	}

	del := httptest.NewRequestWithContext(t.Context(), http.MethodPost,
		"http://example.com/admin/news/delete?scope=launcher&slug="+url.QueryEscape(slug), nil)
	w = httptest.NewRecorder()
	h.Delete(w, del)
	if w.Code != http.StatusOK {
		t.Fatalf("delete: expected 200, got %d (%s)", w.Code, w.Body.String())
	}
}

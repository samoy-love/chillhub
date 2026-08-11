package main

import (
	"net/http"
	"net/http/httptest"
	"runtime"
	"strings"
	"testing"

	"ChillHub/server/internal/httpx"
)

func testServer(t *testing.T) *server {
	t.Helper()
	return newServer(t.TempDir())
}

func TestMutatingHandlersRejectGET(t *testing.T) {
	s := testServer(t)
	cases := []struct {
		name string
		h    http.HandlerFunc
		url  string
	}{
		{name: "activate", h: s.builds.Activate, url: "http://example.com/admin/activate?gameId=launcher&version=1.0.0"},
		{name: "deleteVersion", h: s.builds.DeleteVersion, url: "http://example.com/admin/deleteVersion?gameId=launcher&version=1.0.0"},
		{name: "upload", h: s.builds.Upload, url: "http://example.com/admin/upload"},
		{name: "uploadStream", h: s.builds.UploadStream, url: "http://example.com/admin/uploadStream"},

		{name: "feedbackDelete", h: s.feedback.Delete, url: "http://example.com/admin/feedback/delete?id=1"},
		{name: "feedbackToggleImportant", h: s.feedback.ToggleImportant, url: "http://example.com/admin/feedback/toggleImportant?id=1"},
		{name: "feedbackMarkRead", h: s.feedback.MarkRead, url: "http://example.com/admin/feedback/markRead?id=1"},
		{name: "feedbackMarkUnread", h: s.feedback.MarkUnread, url: "http://example.com/admin/feedback/markUnread?id=1"},
		{name: "feedbackClear", h: s.feedback.Clear, url: "http://example.com/admin/feedback/clear"},

		{name: "gamesSave", h: s.games.Save, url: "http://example.com/admin/games/save"},
		{name: "gameIconUpload", h: s.games.IconUpload, url: "http://example.com/admin/games/icon/upload?gameId=test"},

		{name: "newsRebuild", h: s.news.Rebuild, url: "http://example.com/admin/news/rebuild?scope=global"},
		{name: "newsSave", h: s.news.Save, url: "http://example.com/admin/news/save"},
		{name: "newsDelete", h: s.news.Delete, url: "http://example.com/admin/news/delete?scope=global&slug=test"},
		{name: "newsPublish", h: s.news.Publish, url: "http://example.com/admin/news/publish"},
		{name: "newsPreview", h: s.news.Preview, url: "http://example.com/admin/news/preview"},
		{name: "newsUploadCover", h: s.news.UploadCover, url: "http://example.com/admin/news/uploadCover"},

		{name: "newsAssetsMkdir", h: s.news.AssetsMkdir, url: "http://example.com/admin/news/assets/mkdir"},
		{name: "newsAssetsUpload", h: s.news.AssetsUpload, url: "http://example.com/admin/news/assets/upload"},
		{name: "newsAssetsUploadByURL", h: s.news.AssetsUploadByURL, url: "http://example.com/admin/news/assets/uploadByUrl"},
		{name: "newsAssetsDelete", h: s.news.AssetsDelete, url: "http://example.com/admin/news/assets/delete"},
		{name: "newsAssetsRename", h: s.news.AssetsRename, url: "http://example.com/admin/news/assets/rename"},

		{name: "maintenanceSet", h: s.maintenance.Set, url: "http://example.com/admin/maintenance/set"},
		{name: "maintenanceClear", h: s.maintenance.Clear, url: "http://example.com/admin/maintenance/clear"},

		{name: "metricsClear", h: s.metrics.Clear, url: "http://example.com/admin/metrics/clear"},
		{name: "metricsReport", h: s.metrics.Submit, url: "http://example.com/metrics/report"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequestWithContext(t.Context(), http.MethodGet, tc.url, nil)
			w := httptest.NewRecorder()
			tc.h(w, req)
			if w.Code != http.StatusMethodNotAllowed {
				t.Fatalf("expected %d, got %d", http.StatusMethodNotAllowed, w.Code)
			}
		})
	}
}

// wantPaths is the full HTTP surface of the admin server, excluding the four
// static trees which are only mounted when their directory exists. Any route
// that disappears (or appears) shows up here — exactly the mistake the
// hand-duplicated registrations used to allow.
var wantPaths = []string{
	"/admin",
	"/admin/",
	"/admin/activate",
	"/admin/api",
	"/admin/api/activate",
	"/admin/api/auth/login",
	"/admin/api/auth/logout",
	"/admin/api/auth/me",
	"/admin/api/auth/refresh",
	"/admin/api/auth/verify",
	"/admin/api/deleteVersion",
	"/admin/api/feedback/clear",
	"/admin/api/feedback/delete",
	"/admin/api/feedback/get",
	"/admin/api/feedback/list",
	"/admin/api/feedback/markRead",
	"/admin/api/feedback/markUnread",
	"/admin/api/feedback/toggleImportant",
	"/admin/api/games",
	"/admin/api/games/icon/upload",
	"/admin/api/games/save",
	"/admin/api/games/scan",
	"/admin/api/health",
	"/admin/api/list",
	"/admin/api/maintenance/clear",
	"/admin/api/maintenance/get",
	"/admin/api/maintenance/set",
	"/admin/api/metrics/clear",
	"/admin/api/metrics/errors",
	"/admin/api/metrics/summary",
	"/admin/api/news/assets",
	"/admin/api/news/assets/delete",
	"/admin/api/news/assets/mkdir",
	"/admin/api/news/assets/rename",
	"/admin/api/news/assets/upload",
	"/admin/api/news/assets/uploadByUrl",
	"/admin/api/news/delete",
	"/admin/api/news/get",
	"/admin/api/news/list",
	"/admin/api/news/preview",
	"/admin/api/news/publish",
	"/admin/api/news/rebuild",
	"/admin/api/news/save",
	"/admin/api/news/uploadCover",
	"/admin/api/system/free",
	"/admin/api/upload",
	"/admin/api/upload/abort",
	"/admin/api/upload/chunk",
	"/admin/api/upload/cleanup",
	"/admin/api/upload/complete",
	"/admin/api/upload/init",
	"/admin/api/upload/process",
	"/admin/api/upload/status",
	"/admin/api/uploadStream",
	"/admin/deleteVersion",
	"/admin/feedback/clear",
	"/admin/feedback/delete",
	"/admin/feedback/get",
	"/admin/feedback/list",
	"/admin/feedback/markRead",
	"/admin/feedback/markUnread",
	"/admin/feedback/toggleImportant",
	"/admin/games",
	"/admin/games/icon/upload",
	"/admin/games/save",
	"/admin/games/scan",
	"/admin/health",
	"/admin/list",
	"/admin/maintenance/clear",
	"/admin/maintenance/get",
	"/admin/maintenance/set",
	"/admin/metrics/clear",
	"/admin/metrics/errors",
	"/admin/metrics/summary",
	"/admin/news/assets",
	"/admin/news/assets/delete",
	"/admin/news/assets/mkdir",
	"/admin/news/assets/rename",
	"/admin/news/assets/upload",
	"/admin/news/assets/uploadByUrl",
	"/admin/news/delete",
	"/admin/news/get",
	"/admin/news/list",
	"/admin/news/preview",
	"/admin/news/publish",
	"/admin/news/rebuild",
	"/admin/news/save",
	"/admin/news/uploadCover",
	"/admin/system/free",
	"/admin/upload",
	"/admin/uploadStream",
	"/feedback/submit",
	"/metrics/report",
}

// staticPaths are mounted conditionally (only when their directory exists), so
// they may be absent — but they must never change spelling.
var staticPaths = map[string]bool{
	"/admin/ui/":  true,
	"/assets/":    true,
	"/manifests/": true,
	"/news/":      true,
}

func TestRegisteredPathsMatchContract(t *testing.T) {
	s := testServer(t)
	got := s.register(http.NewServeMux())

	have := make(map[string]bool, len(got))
	for _, p := range got {
		if staticPaths[p] {
			continue
		}
		if have[p] {
			t.Errorf("path %q registered twice", p)
		}
		have[p] = true
	}
	for _, p := range wantPaths {
		if !have[p] {
			t.Errorf("route %q is no longer registered", p)
		}
		delete(have, p)
	}
	for p := range have {
		t.Errorf("unexpected new route %q", p)
	}
}

func TestAliasOf(t *testing.T) {
	cases := map[string]string{
		"/admin/api/news/list": "/admin/news/list",
		"/admin/api/health":    "/admin/health",
		"/admin/api":           "",
		"/feedback/submit":     "",
	}
	for in, want := range cases {
		if got := aliasOf(in); got != want {
			t.Errorf("aliasOf(%q) = %q, want %q", in, got, want)
		}
	}
}

// The public feedback endpoint must stay rate limited.
func TestFeedbackSubmitRateLimited(t *testing.T) {
	s := testServer(t)
	h := s.feedbackLimiter.Wrap(s.feedback.Submit, http.MethodPost)
	limited := false
	for range feedbackRateLimit + 2 {
		req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://example.com/feedback/submit", nil)
		req.RemoteAddr = "10.0.0.1:1234"
		w := httptest.NewRecorder()
		h(w, req)
		if w.Code == http.StatusTooManyRequests {
			limited = true
		}
	}
	if !limited {
		t.Fatal("feedback submit was never rate limited")
	}
}

// The login endpoint must be rate limited through the registered mux, not just
// in theory: an unlimited bcrypt cost-12 comparison is both an online password
// oracle and a CPU exhaustion vector against the public endpoints this same
// process serves. The request goes through s.register so that dropping the
// wrapper from the route table fails the test.
func TestAdminLoginRateLimited(t *testing.T) {
	s := testServer(t)
	mux := http.NewServeMux()
	s.register(mux)

	codes := make([]int, 0, loginRateLimit+2)
	for range loginRateLimit + 2 {
		req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://example.com/admin/api/auth/login",
			strings.NewReader(`{"username":"admin","password":"wrong"}`))
		req.Header.Set("Content-Type", "application/json")
		req.RemoteAddr = "10.0.0.3:1234"
		w := httptest.NewRecorder()
		mux.ServeHTTP(w, req)
		codes = append(codes, w.Code)
	}
	if codes[len(codes)-1] != http.StatusTooManyRequests {
		t.Fatalf("login was never rate limited: codes=%v", codes)
	}
	// The budget must not be global: a different client address still gets in.
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://example.com/admin/api/auth/login",
		strings.NewReader(`{"username":"admin","password":"wrong"}`))
	req.Header.Set("Content-Type", "application/json")
	req.RemoteAddr = "10.0.0.4:1234"
	w := httptest.NewRecorder()
	mux.ServeHTTP(w, req)
	if w.Code == http.StatusTooManyRequests {
		t.Fatal("login limiter is global, expected per-client budget")
	}
}

// The public metrics ingest must stay rate limited too — it is the other
// unauthenticated write endpoint.
func TestMetricsReportRateLimited(t *testing.T) {
	s := testServer(t)
	h := s.metricsLimiter.Wrap(s.metrics.Submit, http.MethodPost)
	limited := false
	for range metricsRateLimit + 2 {
		req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://example.com/metrics/report",
			strings.NewReader(`{"event":"launcher_start"}`))
		req.RemoteAddr = "10.0.0.2:1234"
		w := httptest.NewRecorder()
		h(w, req)
		if w.Code == http.StatusTooManyRequests {
			limited = true
		}
	}
	if !limited {
		t.Fatal("metrics report was never rate limited")
	}
}

// configureMaxProcs replaced an init function, so it is no longer exercised by
// merely importing the package. It must stay callable and must never leave
// GOMAXPROCS at a value that would stop the scheduler.
func TestConfigureMaxProcs(t *testing.T) {
	configureMaxProcs()
	if n := runtime.GOMAXPROCS(0); n < 1 {
		t.Fatalf("GOMAXPROCS = %d after configureMaxProcs", n)
	}
}

// adminCORSOrigin must not hand out a wildcard: the admin API authenticates
// with cookies.
func TestAdminCORSOriginDefaultsToDisabled(t *testing.T) {
	t.Setenv("ADMIN_CORS_ORIGIN", "")
	if got := adminCORSOrigin(); got != httpx.CORSDisabled {
		t.Fatalf("default origin = %q, want the disabled marker", got)
	}
	t.Setenv("ADMIN_CORS_ORIGIN", " https://admin.example.com ")
	if got := adminCORSOrigin(); got != "https://admin.example.com" {
		t.Fatalf("configured origin = %q", got)
	}
}

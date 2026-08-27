package main

import (
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"ChillHub/server/internal/httpx"
)

// The admin API is reachable under two prefixes: nginx proxies /admin/api/... ,
// while direct access (and older UI builds) use /admin/... . Instead of
// registering both by hand — which is how routes used to go missing — every
// entry below is declared once under its canonical /admin/api path and the
// /admin alias is derived automatically.
//
// Endpoints that must NOT be mirrored (auth, the chunked upload sub-tree and
// the /admin/api entry point itself) set noAlias, because their /admin/...
// forms either mean something different or are not part of the contract.
type route struct {
	path    string
	handler http.HandlerFunc
	// noAlias suppresses the derived /admin/... path for this entry.
	noAlias bool
}

// aliasOf returns the /admin/... form of an /admin/api/... path, or "" when the
// path has no alias form.
func aliasOf(path string) string {
	const apiPrefix = "/admin/api/"
	if !strings.HasPrefix(path, apiPrefix) {
		return ""
	}
	return "/admin/" + strings.TrimPrefix(path, apiPrefix)
}

// apiRoutes lists every canonical admin endpoint. Adding one here registers
// both prefixes; there is no second list to keep in sync.
func (s *server) apiRoutes() []route {
	b, n, g, f, gg := s.builds, s.news, s.games, s.feedback, s.gamegallery
	md := s.mods
	mt, mx := s.maintenance, s.metrics
	return []route{
		// Health probe (allowlisted in the auth middleware).
		{path: "/admin/api/health", handler: func(w http.ResponseWriter, _ *http.Request) { _, _ = fmt.Fprintln(w, "ok") }},

		// Session endpoints; nginx routes these verbatim, they have no /admin alias.
		//
		// Login carries its own (tight) budget: it is unauthenticated and each
		// attempt burns a bcrypt cost-12 comparison, so it is both the online
		// password-guessing surface and the cheapest way to saturate the CPU of a
		// process that also answers the public /feedback/submit and
		// /metrics/report endpoints.
		{path: "/admin/api/auth/login", handler: s.prom.count(s.prom.logins, s.loginLimiter.Wrap(s.auth.HandleLogin, http.MethodPost)), noAlias: true},
		{path: "/admin/api/auth/logout", handler: s.auth.HandleLogout, noAlias: true},
		{path: "/admin/api/auth/refresh", handler: s.auth.HandleRefresh, noAlias: true},
		{path: "/admin/api/auth/me", handler: s.auth.HandleMe, noAlias: true},
		{path: "/admin/api/auth/verify", handler: s.auth.HandleVerify, noAlias: true},

		// Builds and versions.
		{path: "/admin/api/list", handler: b.ListVersions},
		// Активация — момент, когда сборка становится «последней» для всех
		// лаунчеров сразу. Без отметки на графике всплеск установок или ошибок
		// после неё выглядит как погода, а не как следствие выкатки.
		{path: "/admin/api/activate", handler: s.prom.count(s.prom.activations, b.Activate)},
		{path: "/admin/api/deleteVersion", handler: b.DeleteVersion},
		{path: "/admin/api/upload", handler: b.Upload},
		{path: "/admin/api/uploadStream", handler: b.UploadStream},

		// Chunked upload; the client always calls the /admin/api form.
		{path: "/admin/api/upload/init", handler: b.UploadInit, noAlias: true},
		{path: "/admin/api/upload/chunk", handler: b.UploadChunk, noAlias: true},
		{path: "/admin/api/upload/status", handler: b.UploadStatus, noAlias: true},
		{path: "/admin/api/upload/complete", handler: b.UploadComplete, noAlias: true},
		{path: "/admin/api/upload/process", handler: b.UploadProcessStream, noAlias: true},
		{path: "/admin/api/upload/cleanup", handler: b.UploadCleanup, noAlias: true},
		{path: "/admin/api/upload/abort", handler: b.UploadAbort, noAlias: true},

		// System info.
		{path: "/admin/api/system/free", handler: b.FreeSpace},

		// Feedback inbox (the public submit endpoint is registered separately).
		{path: "/admin/api/feedback/list", handler: f.List},
		{path: "/admin/api/feedback/get", handler: f.Get},
		{path: "/admin/api/feedback/delete", handler: f.Delete},
		{path: "/admin/api/feedback/toggleImportant", handler: f.ToggleImportant},
		{path: "/admin/api/feedback/markRead", handler: f.MarkRead},
		{path: "/admin/api/feedback/markUnread", handler: f.MarkUnread},
		{path: "/admin/api/feedback/clear", handler: f.Clear},

		// Maintenance mode. The launcher reads the state from the PUBLIC API
		// (GET /api/maintenance on :55700); these three only write it.
		{path: "/admin/api/maintenance/get", handler: mt.Get},
		{path: "/admin/api/maintenance/set", handler: s.prom.count(s.prom.maintenance, mt.Set, "set")},
		{path: "/admin/api/maintenance/clear", handler: s.prom.count(s.prom.maintenance, mt.Clear, "clear")},

		// Launcher metrics (the public ingest endpoint is registered separately).
		{path: "/admin/api/metrics/summary", handler: mx.Summary},
		// Раскрытие кода ошибки в конкретные события: сводка говорит «sync_failed — 8»,
		// а на какой версии и в какой игре — видно только здесь.
		{path: "/admin/api/metrics/errors", handler: mx.ErrorEvents},
		{path: "/admin/api/metrics/clear", handler: mx.Clear},

		// News management.
		{path: "/admin/api/news/list", handler: n.List},
		{path: "/admin/api/news/get", handler: n.Get},
		{path: "/admin/api/news/save", handler: n.Save},
		{path: "/admin/api/news/delete", handler: n.Delete},
		{path: "/admin/api/news/rebuild", handler: n.Rebuild},
		{path: "/admin/api/news/publish", handler: n.Publish},
		{path: "/admin/api/news/preview", handler: n.Preview},
		{path: "/admin/api/news/uploadCover", handler: n.UploadCover},

		// Assets gallery.
		{path: "/admin/api/news/assets", handler: n.AssetsList},
		{path: "/admin/api/news/assets/mkdir", handler: n.AssetsMkdir},
		{path: "/admin/api/news/assets/upload", handler: n.AssetsUpload},
		{path: "/admin/api/news/assets/uploadByUrl", handler: n.AssetsUploadByURL},
		{path: "/admin/api/news/assets/delete", handler: n.AssetsDelete},
		{path: "/admin/api/news/assets/rename", handler: n.AssetsRename},

		// Games registry.
		{path: "/admin/api/games", handler: g.Get},
		{path: "/admin/api/games/save", handler: g.Save},
		{path: "/admin/api/games/icon/upload", handler: g.IconUpload},
		{path: "/admin/api/games/scan", handler: g.Scan},
		{path: "/admin/api/games/purge", handler: g.Purge},

		// Modpacks. Every one of these talks to Thunderstore from THIS process,
		// never from the panel's browser: the panel's fetch wrapper rewrites
		// paths and attaches a CSRF token, and pointing it at a third-party
		// host would trip CORS and leak panel traffic there at the same time.
		{path: "/admin/api/games/ecosystem", handler: md.Ecosystem},
		{path: "/admin/api/mods/catalog", handler: md.Catalog},
		{path: "/admin/api/mods/readme", handler: md.Readme},
		{path: "/admin/api/mods/resolve", handler: md.Resolve},
		{path: "/admin/api/mods/list", handler: md.List},
		{path: "/admin/api/mods/activate", handler: md.Activate},
		{path: "/admin/api/mods/deleteVersion", handler: md.DeleteVersion},
		{path: "/admin/api/mods/diff", handler: md.Diff},
		{path: "/admin/api/mods/cache", handler: md.Cache},
		// The two streaming endpoints keep the /admin/api form only: nginx
		// proxies them verbatim and their NDJSON bodies must not be buffered
		// by an alias route nobody configured.
		{path: "/admin/api/mods/build", handler: md.Build, noAlias: true},
		{path: "/admin/api/mods/import", handler: md.Import, noAlias: true},

		// Per-game screenshot gallery.
		{path: "/admin/api/games/gallery", handler: gg.List},
		{path: "/admin/api/games/gallery/mkdir", handler: gg.Mkdir},
		{path: "/admin/api/games/gallery/upload", handler: gg.Upload},
		{path: "/admin/api/games/gallery/uploadByUrl", handler: gg.UploadByURL},
		{path: "/admin/api/games/gallery/delete", handler: gg.Delete},
		{path: "/admin/api/games/gallery/rename", handler: gg.Rename},
		{path: "/admin/api/games/gallery/setCover", handler: gg.SetCoverHandler},
		{path: "/admin/api/games/gallery/setCaption", handler: gg.SetCaptionHandler},
	}
}

// register wires every route (plus its alias) into mux and returns the full
// list of registered paths, sorted — handy for tests and for the boot log.
func (s *server) register(mux *http.ServeMux) []string {
	var paths []string
	add := func(p string, h http.Handler) {
		mux.Handle(p, h)
		paths = append(paths, p)
	}

	for _, rt := range s.apiRoutes() {
		add(rt.path, rt.handler)
		if rt.noAlias {
			continue
		}
		if alias := aliasOf(rt.path); alias != "" {
			add(alias, rt.handler)
		}
	}

	// Admin UI entry points. /admin/api serves the UI too (historic behaviour of
	// a bare API prefix hit from a browser); /admin redirects to /admin/ so that
	// relative asset links resolve.
	add("/admin/", http.HandlerFunc(s.handleAdminUI))
	add("/admin/api", http.HandlerFunc(s.handleAdminUI))
	add("/admin", http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Normalize to trailing slash for relative asset links
		http.Redirect(w, r, "/admin/", http.StatusFound)
	}))

	// Public feedback submit (no auth; allowlisted in the auth middleware and
	// rate limited per client IP).
	add("/feedback/submit", s.prom.count(s.prom.feedback, s.feedbackLimiter.Wrap(s.feedback.Submit, http.MethodPost)))

	// Public metrics ingest (no auth; same shape as /feedback/submit — outside
	// the /admin/ prefix, so the auth middleware never sees it, and rate limited
	// per client IP). It lives on the admin process rather than the public API
	// because this process is the single writer of the events file.
	add("/metrics/report", s.metricsLimiter.Wrap(s.metrics.Submit, http.MethodPost))

	// Static trees. Serving news/assets/manifests here lets the admin UI display
	// images without an external nginx; each is only mounted when it exists.
	newsDir := filepath.Join(s.contentRoot, "news")
	if isDir(newsDir) {
		add("/news/", httpx.NoStore(http.StripPrefix("/news/", http.FileServer(http.Dir(newsDir)))))
	}
	assetsDir := filepath.Join(newsDir, "assets")
	if isDir(assetsDir) {
		add("/assets/", httpx.NoStore(http.StripPrefix("/assets/", http.FileServer(http.Dir(assetsDir)))))
	}
	manifestsDir := filepath.Join(s.contentRoot, "manifests")
	if isDir(manifestsDir) {
		add("/manifests/", httpx.NoStore(http.StripPrefix("/manifests/", http.FileServer(http.Dir(manifestsDir)))))
	}
	// content/<gameId>/gallery/... — gamegallery.go builds preview URLs as
	// /content/<gid>/gallery/<file> assuming the public API's
	// PathPrefix("/content/") mount, which this admin process never had;
	// without this the admin UI's own gallery tab 404s on every uploaded
	// screenshot. Deliberately scoped to just that subtree, not the whole
	// content/ root: the admin auth middleware only gates paths under /admin/,
	// so mounting all of content/ here would make every game's unpacked build,
	// plus a raw directory listing, reachable from this origin with no login.
	// The public API process still serves all of content/ on its own, separate,
	// origin.
	contentDir := filepath.Join(s.contentRoot, "content")
	if isDir(contentDir) {
		add("/content/", httpx.NoStore(http.StripPrefix("/content/", galleryOnly(http.Dir(contentDir)))))
	}
	// Static Admin UI assets from server/admin_ui
	uiDir := detectAdminUIDir()
	if isDir(uiDir) {
		add("/admin/ui/", httpx.NoStore(http.StripPrefix("/admin/ui/", http.FileServer(http.Dir(uiDir)))))
	}

	sort.Strings(paths)
	return paths
}

// galleryOnly wraps a content/ file server so it only ever serves paths of the
// shape "<gameId>/gallery/<...>/<file>" — never a bare directory (which
// http.FileServer would otherwise list) and never anything outside gallery/
// (in particular, never the unpacked builds in content/<gameId>/<version>/,
// which this unauthenticated mount was never meant to expose — see the comment
// at its call site).
func galleryOnly(root http.FileSystem) http.Handler {
	fs := http.FileServer(root)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		segments := strings.Split(strings.Trim(r.URL.Path, "/"), "/")
		// Order is load-bearing here, not just style: len(segments) < 3 must
		// short-circuit before segments[1]/segments[len-1] are indexed, or a
		// 0-1 segment path (e.g. GET /content/x) panics with index out of range.
		if len(segments) < 3 || segments[1] != "gallery" || segments[len(segments)-1] == "" {
			http.NotFound(w, r)
			return
		}
		fs.ServeHTTP(w, r)
	})
}

func isDir(p string) bool {
	st, err := os.Stat(p)
	return err == nil && st.IsDir()
}

// Command admin serves the ChillHub admin API and UI on :55777.
//
// The request-handling code lives in server/internal/adminapi/* (one package
// per domain); this file only wires those handlers together, applies the
// middleware chain and starts the server.
package main

import (
	"log"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"

	"ChillHub/server/internal/adminapi/auth"
	"ChillHub/server/internal/adminapi/builds"
	"ChillHub/server/internal/adminapi/feedback"
	"ChillHub/server/internal/adminapi/games"
	"ChillHub/server/internal/adminapi/news"
	"ChillHub/server/internal/adminutil"
	"ChillHub/server/internal/httpx"
	"ChillHub/server/internal/ratelimit"

	"go.uber.org/automaxprocs/maxprocs"
)

// Public feedback submit is the only unauthenticated write endpoint, so it
// keeps its own budget: 5 reports per minute per client address.
const (
	feedbackRateLimit  = 5
	feedbackRateWindow = time.Minute
)

// server owns the content root and the per-domain handler sets. Nothing here is
// package-level state: the handlers receive the root explicitly, which is what
// lets the tests point them at a temporary directory.
type server struct {
	contentRoot string
	auth        *auth.Auth
	builds      *builds.Handlers
	news        *news.Handlers
	games       *games.Handlers
	feedback    *feedback.Handlers

	feedbackLimiter *ratelimit.Limiter
}

func newServer(contentRoot string) *server {
	a := auth.New(auth.LoadConfig())
	b := builds.New(contentRoot)
	b.CurrentUser = a.CurrentUser
	f := feedback.New(contentRoot)
	f.CurrentUser = a.CurrentUser
	return &server{
		contentRoot:     contentRoot,
		auth:            a,
		builds:          b,
		news:            news.New(contentRoot),
		games:           games.New(contentRoot),
		feedback:        f,
		feedbackLimiter: ratelimit.New(feedbackRateLimit, feedbackRateWindow),
	}
}

func init() {
	// Configure GOMAXPROCS automatically. On Windows (no cgroup quotas) suppress noisy info message.
	_, err := maxprocs.Set(maxprocs.Logger(func(format string, a ...any) {
		if runtime.GOOS == "windows" {
			return
		}
		log.Printf("[maxprocs] "+format, a...)
	}))
	if err != nil {
		log.Printf("[maxprocs] set failed: %v", err)
	}
}

// adminCORSOrigin returns the CORS origin spec for the admin API.
//
// The admin API authenticates with cookies, so a wildcard origin must not be
// the default. The admin UI is served from this very process (same origin), so
// cross-origin access is off unless ADMIN_CORS_ORIGIN names explicit origins
// (comma-separated), e.g. "https://admin.example.com".
func adminCORSOrigin() string {
	if v := strings.TrimSpace(os.Getenv("ADMIN_CORS_ORIGIN")); v != "" {
		return v
	}
	return httpx.CORSDisabled
}

func main() {
	contentRoot := adminutil.DetectContentRoot()
	s := newServer(contentRoot)

	mux := http.NewServeMux()
	paths := s.register(mux)
	go s.builds.StartUploadJanitor()

	addr := ":55777"
	log.Printf("admin API listening on %s (CONTENT_ROOT=%s, routes=%d)", addr, contentRoot, len(paths))
	// Middlewares: RequestID -> CORS -> Auth -> Logging
	var h http.Handler = mux
	h = httpx.RequestID()(h)
	h = httpx.CORS(adminCORSOrigin())(h)
	h = s.auth.Middleware(h)
	h = httpx.Logging("ADMIN")(h)
	srv := &http.Server{
		Addr:              addr,
		Handler:           h,
		ReadHeaderTimeout: 10 * time.Second,
		// ReadTimeout/WriteTimeout ДОЛЖНЫ оставаться нулевыми (без ограничения).
		// Админ принимает многогигабайтные ZIP-сборки (nginx: client_max_body_size 30g,
		// таймауты 6h) и отдаёт NDJSON-прогресс распаковки, идущий минутами.
		// Любое конечное значение здесь обрывает загрузку и стриминг на середине —
		// 30s убивали загрузку почти сразу. От slowloris защищает ReadHeaderTimeout.
		ReadTimeout:  0,
		WriteTimeout: 0,
		IdleTimeout:  120 * time.Second,
	}
	log.Fatal(srv.ListenAndServe())
}

// handleAdminUI serves the login page for anonymous visitors and the dashboard
// for authenticated ones.
func (s *server) handleAdminUI(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	// If not authenticated, serve login page; otherwise serve admin dashboard
	uiDir := detectAdminUIDir()
	if s.auth.CurrentUser(r) == "" {
		http.ServeFile(w, r, filepath.Join(uiDir, "login.html"))
		return
	}
	http.ServeFile(w, r, filepath.Join(uiDir, "admin.html"))
}

func detectAdminUIDir() string {
	// 1) alongside executable: ../server/admin_ui or ./admin_ui
	if exe, err := os.Executable(); err == nil && exe != "" {
		d := filepath.Dir(exe)
		// try ./admin_ui relative to exe
		p1 := filepath.Join(d, "admin_ui")
		if isDir(p1) {
			return p1
		}
		// try ../server/admin_ui (dev run from server/cmd/admin/...)
		p2 := filepath.Clean(filepath.Join(d, "..", "..", "admin_ui"))
		if isDir(p2) {
			return p2
		}
		// try ../../server/admin_ui
		p3 := filepath.Clean(filepath.Join(d, "..", "admin_ui"))
		if isDir(p3) {
			return p3
		}
	}
	// 2) fallback: walk up to 6 levels from working directory and try server/admin_ui and admin_ui
	wd, _ := os.Getwd()
	cur := wd
	for i := 0; i < 6; i++ {
		cand1 := filepath.Join(cur, "server", "admin_ui")
		if isDir(cand1) {
			return cand1
		}
		cand2 := filepath.Join(cur, "admin_ui")
		if isDir(cand2) {
			return cand2
		}
		parent := filepath.Dir(cur)
		if parent == cur {
			break
		}
		cur = parent
	}
	return "server/admin_ui"
}

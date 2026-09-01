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
	"slices"
	"strings"
	"time"

	"ChillHub/server/internal/adminapi/auth"
	"ChillHub/server/internal/adminapi/builds"
	"ChillHub/server/internal/adminapi/feedback"
	"ChillHub/server/internal/adminapi/gamegallery"
	"ChillHub/server/internal/adminapi/games"
	"ChillHub/server/internal/adminapi/mods"
	"ChillHub/server/internal/adminapi/news"
	"ChillHub/server/internal/adminutil"
	"ChillHub/server/internal/httpx"
	"ChillHub/server/internal/maintenance"
	"ChillHub/server/internal/metrics"
	"ChillHub/server/internal/promexp"
	"ChillHub/server/internal/ratelimit"

	"go.uber.org/automaxprocs/maxprocs"
)

// Unauthenticated write endpoints get their own budgets per client address.
//
// Feedback is typed by a human, so 5 per minute is already generous. Metrics
// are emitted by the launcher itself: a start event, then a handful around each
// install or update, so 30 per minute leaves room for a burst (several games
// updated back to back) while still capping a runaway retry loop.
const (
	feedbackRateLimit  = 5
	feedbackRateWindow = time.Minute

	metricsRateLimit  = 30
	metricsRateWindow = time.Minute

	// The login endpoint is unauthenticated by definition and every attempt
	// costs a bcrypt comparison at cost 12 (~250 ms of CPU on the production
	// ARM64 box). Without a budget a single client can both brute-force the
	// password online and starve this process — which also serves the public
	// /feedback/submit and /metrics/report endpoints — with a handful of
	// concurrent POSTs. Ten attempts per five minutes is far more than a human
	// mistyping a password needs and reduces an online guessing rate to
	// something useless.
	loginRateLimit  = 10
	loginRateWindow = 5 * time.Minute
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
	gamegallery *gamegallery.Handlers
	mods        *mods.Handlers
	feedback    *feedback.Handlers
	maintenance *maintenance.Store
	metrics     *metrics.Handlers
	prom        *adminMetrics

	feedbackLimiter *ratelimit.Limiter
	metricsLimiter  *ratelimit.Limiter
	loginLimiter    *ratelimit.Limiter
}

func newServer(contentRoot string) *server {
	a := auth.New(auth.LoadConfig())
	b := builds.New(contentRoot)
	b.CurrentUser = a.CurrentUser
	f := feedback.New(contentRoot)
	f.CurrentUser = a.CurrentUser
	mt := maintenance.New(contentRoot)
	mt.CurrentUser = a.CurrentUser
	mx := metrics.New(contentRoot)
	mx.CurrentUser = a.CurrentUser

	// Свой реестр на каждый экземпляр сервера: имя метрики регистрируется
	// однократно, а тесты поднимают сервер по нескольку раз за прогон.
	reg := promexp.New()
	mx.Prom = metrics.NewProduct(reg)

	g := games.New(contentRoot)

	return &server{
		contentRoot:     contentRoot,
		auth:            a,
		builds:          b,
		news:            news.New(contentRoot),
		games:           g,
		mods:            mods.New(contentRoot, b, g),
		gamegallery:     gamegallery.New(contentRoot),
		feedback:        f,
		maintenance:     mt,
		metrics:         mx,
		prom:            newAdminMetrics(reg, mt),
		feedbackLimiter: ratelimit.New(feedbackRateLimit, feedbackRateWindow),
		metricsLimiter:  ratelimit.New(metricsRateLimit, metricsRateWindow),
		loginLimiter:    ratelimit.New(loginRateLimit, loginRateWindow),
	}
}

// configureMaxProcs matches GOMAXPROCS to the cgroup quota. It lives in main
// rather than in an init function so that it runs when the command runs, not
// whenever something imports this package.
func configureMaxProcs() {
	// On Windows (no cgroup quotas) suppress the noisy info message.
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

// middleware оборачивает маршрутизатор цепочкой админского API.
//
// Порядок в chain — это порядок, в котором обёртки встречает ЗАПРОС: первая
// снаружи, последняя ближе всех к хендлеру. Обёртки навешиваются с конца
// списка именно поэтому. Раньше их перечисляли в этом же порядке, но
// накладывали построчно сверху вниз, и цепочка исполнялась задом наперёд:
// заданный ADMIN_CORS_ORIGIN не спасал preflight, который упирался в 401
// раньше CORS, а X-Request-Id, выданный клиенту, не попадал ни в одну строку
// журнала — RequestID оказывался внутри Logging. Публичный API (cmd/api)
// собран правильно с самого начала, и теперь обе цепочки читаются одинаково.
//
// ЖУРНАЛ СНАРУЖИ АВТОРИЗАЦИИ И CORS — по той же причине, что и счётчик. Обе
// эти обёртки отвечают САМИ и хендлер не зовут: авторизация — 401, CORS —
// 204 на preflight. Стоя внутри них, журнал не увидел бы ни того, ни другого,
// и «у меня истекла сессия» вместе со сканером, перебирающим /admin/api/*,
// пропали бы из него совсем — а это ровно те два случая, ради которых в него
// и смотрят. RequestID при этом остаётся снаружи журнала, чтобы выданный
// клиенту идентификатор попадал в строку.
func (s *server) middleware(h http.Handler, corsOrigin string, route httpx.RouteFunc) http.Handler {
	chain := []func(http.Handler) http.Handler{
		// Счётчик снаружи авторизации: 401 и 429 — это тоже ответы, и всплеск
		// именно таких кодов виден только если их считают.
		httpx.Metrics(s.prom.reg, "admin", route),
		httpx.RequestID(),
		httpx.Logging("ADMIN"),
		httpx.CORS(corsOrigin),
		s.auth.Middleware,
	}
	for _, mw := range slices.Backward(chain) {
		h = mw(h)
	}
	return h
}

func main() {
	configureMaxProcs()
	contentRoot := adminutil.DetectContentRoot()
	s := newServer(contentRoot)

	mux := http.NewServeMux()
	paths := s.register(mux)
	go s.builds.StartUploadJanitor()

	// Loopback by default: nginx proxies to 127.0.0.1 and nothing else has any
	// business talking to the admin API directly. ADMIN_LISTEN_ADDR overrides.
	addr := httpx.ListenAddr("ADMIN_LISTEN_ADDR", 55777)
	log.Printf("admin API listening on %s (CONTENT_ROOT=%s, routes=%d)", addr, contentRoot, len(paths))
	// Экспортёр на отдельном порту и по умолчанию на loopback: наружу торчит
	// только nginx, и ни один его location сюда не ведёт.
	go promexp.Serve(httpx.ListenAddr("ADMIN_METRICS_LISTEN_ADDR", 55778), s.prom.reg)

	exact, prefixes := routeLabels(paths)
	h := s.middleware(mux, adminCORSOrigin(), httpx.StaticRoutes(exact, prefixes))
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
	for range 6 {
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

// Command api serves the public launcher API on :55700 — the game registry,
// per-game builds, the published news indexes and the maintenance flag — plus
// the static content trees when it runs without nginx in front of it.
package main

import (
	"encoding/json"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"time"

	"ChillHub/server/internal/adminutil"
	"ChillHub/server/internal/httpx"
	"ChillHub/server/internal/maintenance"
	"ChillHub/server/internal/promexp"
	"ChillHub/server/internal/ratelimit"

	"github.com/gorilla/mux"
	"go.uber.org/automaxprocs/maxprocs"
)

// Rate limiting for the public API. The launcher fans out to several endpoints
// at startup (games, per-game latest, news) and then downloads content from
// /content/ and /manifests/, so the budget is deliberately generous: it exists
// to blunt scraping and accidental retry storms, not to pace a normal client.
//
// Behind nginx the caller's address arrives in X-Forwarded-For / X-Real-IP,
// which ratelimit.ClientIP already honours.
const (
	apiRateLimitDefault  = 600
	apiRateWindowDefault = time.Minute
)

// apiRateLimit reads the budget from API_RATE_LIMIT / API_RATE_WINDOW.
// A limit of 0 (or a negative value) disables limiting entirely.
func apiRateLimit() (int, time.Duration) {
	limit := apiRateLimitDefault
	if v := strings.TrimSpace(os.Getenv("API_RATE_LIMIT")); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			limit = n
		}
	}
	window := apiRateWindowDefault
	if v := strings.TrimSpace(os.Getenv("API_RATE_WINDOW")); v != "" {
		if d, err := time.ParseDuration(v); err == nil && d > 0 {
			window = d
		}
	}
	return limit, window
}

type GameInfo struct {
	GameID          string `json:"gameId"`
	Title           string `json:"title"`
	HasLatest       bool   `json:"hasLatest"`
	LatestVersion   string `json:"latestVersion,omitempty"`
	ManifestURL     string `json:"manifestUrl,omitempty"`
	ExeRelativePath string `json:"exeRelativePath,omitempty"`
	IconURL         string `json:"iconUrl,omitempty"`
}

// (moved to server/internal/httpx)

type GamesResponse struct {
	Items []GameInfo `json:"items"`
}

// isDir отвечает, существует ли путь и каталог ли это.
func isDir(path string) bool {
	stat, err := os.Stat(path)
	return err == nil && stat.IsDir()
}

// resolveContentRoot определяет каталог с контентом.
//
// Явный CONTENT_ROOT — единственный способ, которым это задаётся на проде.
// Всё остальное здесь нужно для запуска из исходников: сначала пробуем путь
// относительно исполняемого файла (он обычно лежит в server/cmd/api), затем —
// относительно рабочего каталога и двух уровней над ним.
//
// Вынесено из main: поиск каталога — самостоятельная задача с собственными
// ветвлениями, и в main от неё нужен ровно один ответ. Заодно main снова стал
// тем, чем должен быть, — списком того, что поднимается при старте.
func resolveContentRoot() string {
	if root := os.Getenv("CONTENT_ROOT"); root != "" {
		return root
	}

	exe, _ := os.Executable()
	if try := filepath.Clean(filepath.Join(filepath.Dir(exe), "..", "..", "..", "content")); isDir(try) {
		return try
	}

	cwd, _ := os.Getwd()
	for _, candidate := range []string{
		filepath.Join(cwd, "content"),
		filepath.Join(cwd, "..", "content"),
		filepath.Join(cwd, "..", "..", "content"),
	} {
		if isDir(candidate) {
			return candidate
		}
	}

	return "content" // последняя надежда: относительный путь от рабочего каталога
}

func main() {
	_, err := maxprocs.Set(maxprocs.Logger(func(format string, a ...any) {
		if runtime.GOOS == "windows" {
			return
		}
		log.Printf("[maxprocs] "+format, a...)
	}))
	if err != nil {
		log.Printf("[maxprocs] set failed: %v", err)
	}
	contentRoot = resolveContentRoot()

	limit, window := apiRateLimit()
	limiter := ratelimit.New(limit, window)
	reg := promexp.New()
	r := newRouter(limiter, reg)

	// Экспортёр живёт на своём порту и по умолчанию на loopback: продуктовые
	// метрики не должны зависеть от того, не появился ли в конфиге nginx ещё
	// один location. Prometheus работает в контейнере и ходит на хост через
	// docker-мост, поэтому на проде адрес задаётся явно.
	go promexp.Serve(httpx.ListenAddr("API_METRICS_LISTEN_ADDR", 55701), reg)

	// Loopback by default: in production nginx proxies to 127.0.0.1. For a dev
	// box that has to be reachable from another machine set API_LISTEN_ADDR.
	addr := httpx.ListenAddr("API_LISTEN_ADDR", 55700)
	if limit > 0 {
		log.Printf("public API rate limit: %d requests per %s per client IP", limit, window)
	} else {
		log.Printf("public API rate limit: disabled")
	}
	log.Printf("public API listening on %s (contentRoot=%s)", addr, contentRoot)
	srv := &http.Server{
		Addr:              addr,
		Handler:           r,
		ReadHeaderTimeout: 10 * time.Second,
		ReadTimeout:       30 * time.Second,
		// WriteTimeout нулевой: в dev этот процесс сам раздаёт /content/ — файлы игр
		// на гигабайты, скачивание которых заведомо длиннее любого разумного лимита.
		// Запросы короткие (GET), поэтому ReadTimeout ограничиваем, а отдачу — нет.
		WriteTimeout: 0,
		IdleTimeout:  120 * time.Second,
	}
	log.Fatal(srv.ListenAndServe())
}

// newRouter wires the public routes. It is separate from main so that tests can
// drive the real routing table (method matching included) without a listener.
func newRouter(limiter *ratelimit.Limiter, reg *promexp.Registry) *mux.Router {
	r := mux.NewRouter()
	// Счётчик первым в цепочке: запрос, отбитый лимитером или CORS, — это тоже
	// ответ, и «всем прилетает 429» должно быть видно на графике, а не только
	// в логе.
	r.Use(httpx.Metrics(reg, "api", muxRoute))
	r.Use(httpx.RequestID())
	r.Use(httpx.CORS("*"))
	r.Use(httpx.Logging("PUBLIC"))

	// The limiter is attached per JSON endpoint, NOT router-wide. In dev this
	// process also serves /content/ and /manifests/, and installing a game is
	// thousands of file requests fanned out over up to 16 download threads —
	// a router-wide budget would trip mid-install and fail the download. In
	// production nginx serves those paths directly, so limiting them here buys
	// nothing anyway. What we do want capped is the cheap-to-request,
	// expensive-to-serve JSON that a scraper or a retry storm would hammer.
	//
	// Every GET route also answers HEAD: RFC 9110 requires HEAD wherever GET is
	// supported, and net/http already produces a headers-only response from the
	// GET handler, so listing the method is all that is needed.
	r.HandleFunc("/api/games", limiter.Wrap(handleGames)).Methods("GET", "HEAD")
	r.HandleFunc("/api/games/{gameId}", limiter.Wrap(handleGame)).Methods("GET", "HEAD")
	r.HandleFunc("/api/games/{gameId}/versions/latest", limiter.Wrap(handleLatest)).Methods("GET", "HEAD")
	r.HandleFunc("/api/games/{gameId}/builds", limiter.Wrap(handleBuilds)).Methods("GET", "HEAD")
	// Maintenance mode flag. Polled by every launcher at startup and on a timer,
	// so it is served from an mtime-checked in-memory cache (see the package
	// doc) and shares the same generous JSON budget as the rest.
	maint := maintenance.New(contentRoot)
	r.HandleFunc("/api/maintenance", limiter.Wrap(maint.PublicHandler)).Methods("GET", "HEAD")
	r.HandleFunc("/news/index.json", limiter.Wrap(handleNewsIndex)).Methods("GET", "HEAD")
	r.HandleFunc("/news/games/{gameId}/index.json", limiter.Wrap(handleGameNewsIndex)).Methods("GET", "HEAD")

	// Serve manifests, content and news statically for local dev (no indirection)
	r.PathPrefix("/manifests/").Handler(httpx.NoStore(http.StripPrefix("/manifests/", http.FileServer(http.Dir(filepath.Join(contentRoot, "manifests"))))))
	r.PathPrefix("/content/").Handler(httpx.NoStore(http.StripPrefix("/content/", http.FileServer(http.Dir(filepath.Join(contentRoot, "content"))))))
	// /news/ already covers /news/games/... — a second, later PathPrefix for it
	// would never be reached, so there is none.
	r.PathPrefix("/news/").Handler(httpx.NoStore(http.StripPrefix("/news/", http.FileServer(http.Dir(filepath.Join(contentRoot, "news"))))))
	r.PathPrefix("/assets/").Handler(httpx.NoStore(http.StripPrefix("/assets/", http.FileServer(http.Dir(filepath.Join(contentRoot, "news", "assets"))))))

	return r
}

var contentRoot string

// muxRoute names the matched route by its path TEMPLATE ("/api/games/{gameId}"),
// not by the requested path. The template is a closed set defined in this file,
// so no request can add a time series; the concrete game id would add one per
// game — and one per typo — for a breakdown that the product counters already
// provide with a bounded label.
func muxRoute(r *http.Request) string {
	if route := mux.CurrentRoute(r); route != nil {
		if tpl, err := route.GetPathTemplate(); err == nil && tpl != "" {
			return tpl
		}
	}
	return "other"
}

// loadGamesFromRegistry attempts to read admin-managed registry:
//
//	content/manifests/_registry/games.json
//
// It returns a slice of GameInfo without latest/manifest fields populated.
func loadGamesFromRegistry() ([]GameInfo, bool) {
	type regItem struct {
		GameID          string `json:"gameId"`
		Title           string `json:"title"`
		ExeRelativePath string `json:"exeRelativePath"`
		IconURL         string `json:"iconUrl"`
	}
	var reg struct {
		Items []regItem `json:"items"`
	}
	p := filepath.Join(contentRoot, "manifests", "_registry", "games.json")
	// #nosec G304 -- p is the content root plus three constant path components.
	// No part of it comes from the request.
	b, err := os.ReadFile(p)
	if err != nil {
		return nil, false
	}
	if json.Unmarshal(b, &reg) != nil || len(reg.Items) == 0 {
		return nil, false
	}
	out := make([]GameInfo, 0, len(reg.Items))
	for _, it := range reg.Items {
		// The stored registry is not a trusted source of path components: every
		// handler below joins an id onto the manifests directory. An id that is
		// not a plausible slug is dropped rather than turned into a path. The
		// admin save endpoint now refuses such ids outright, so reaching this
		// line means the file was edited by hand.
		if !adminutil.IsSafeGameID(it.GameID) {
			log.Printf("registry: skipping entry with unusable gameId %q", filepath.Base(it.GameID))
			continue
		}
		out = append(out, GameInfo{GameID: it.GameID, Title: it.Title, ExeRelativePath: it.ExeRelativePath, IconURL: it.IconURL})
	}
	// An existing registry stays authoritative even when everything in it was
	// dropped. Falling back to a directory scan here would look like a repair
	// but is not one: a scanned entry carries no title and no exe path, so the
	// launcher would list games under raw ids whose Play button does nothing.
	// A visibly empty list plus the log lines above points at the real problem.
	// The scan below is for "no registry yet", which is a different situation.
	return out, true
}

// fallback: scan manifests directory for game IDs (subdirectories)
func loadGamesByScanning() []GameInfo {
	base := filepath.Join(contentRoot, "manifests")
	entries, err := os.ReadDir(base)
	if err != nil {
		return []GameInfo{}
	}
	items := make([]GameInfo, 0)
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		name := e.Name()
		// skip internal registry folder
		if strings.EqualFold(name, "_registry") {
			continue
		}
		items = append(items, GameInfo{GameID: name, Title: name})
	}
	return items
}

// loadGames returns the list of games, preferring admin registry.
func loadGames() []GameInfo {
	if items, ok := loadGamesFromRegistry(); ok {
		return items
	}
	return loadGamesByScanning()
}

func handleGames(w http.ResponseWriter, r *http.Request) {
	base := baseURL(r)
	baseItems := loadGames()
	items := make([]GameInfo, 0, len(baseItems))
	for _, g := range baseItems {
		latestPath := filepath.Join(contentRoot, "manifests", g.GameID, "latest.json")
		latest, ok := readLatest(latestPath)
		item := g
		item.HasLatest = ok
		if ok {
			item.LatestVersion = latest.Version
			item.ManifestURL = base + "/manifests/" + g.GameID + "/" + latest.Version + ".json"
		}
		items = append(items, item)
	}
	writeJSON(w, GamesResponse{Items: items})
}

// maxGameIDLen bounds the {gameId} path variable. Real IDs are short slugs;
// anything longer is either a probe or a mistake and must not reach the disk.
const maxGameIDLen = 64

// publicGameID validates the {gameId} path variable and answers 404 when it is
// not a plausible identifier.
//
// Every admin handler already gates its game id through adminutil.IsSafeGameID;
// the public handlers used to pass the raw value straight to filepath.Join.
// Traversal was blocked by Join's normalisation, but the missing check still
// leaked information: "_registry" is the internal registry directory and
// /api/games/_registry/builds answered 200 with its contents, and a 300-char id
// was happily turned into a stat() call.
func publicGameID(w http.ResponseWriter, r *http.Request) (string, bool) {
	gid := mux.Vars(r)["gameId"]
	// A leading underscore is reserved for internal directories such as
	// _registry, which loadGamesByScanning already skips.
	if len(gid) > maxGameIDLen || strings.HasPrefix(gid, "_") || !adminutil.IsSafeGameID(gid) {
		http.NotFound(w, r)
		return "", false
	}
	return gid, true
}

func handleGame(w http.ResponseWriter, r *http.Request) {
	gid, ok := publicGameID(w, r)
	if !ok {
		return
	}
	base := baseURL(r)
	for _, g := range loadGames() {
		if g.GameID == gid {
			latestPath := filepath.Join(contentRoot, "manifests", g.GameID, "latest.json")
			latest, ok := readLatest(latestPath)
			item := g
			item.HasLatest = ok
			if ok {
				item.LatestVersion = latest.Version
				item.ManifestURL = base + "/manifests/" + g.GameID + "/" + latest.Version + ".json"
			}
			writeJSON(w, item)
			return
		}
	}
	http.NotFound(w, r)
}

func handleLatest(w http.ResponseWriter, r *http.Request) {
	gid, ok := publicGameID(w, r)
	if !ok {
		return
	}
	base := baseURL(r)
	latestPath := filepath.Join(contentRoot, "manifests", gid, "latest.json")
	latest, ok := readLatest(latestPath)
	if !ok {
		writeJSON(w, map[string]any{"gameId": gid, "hasLatest": false})
		return
	}
	writeJSON(w, map[string]any{
		"gameId":      gid,
		"version":     latest.Version,
		"manifestUrl": base + "/manifests/" + gid + "/" + latest.Version + ".json",
	})
}

type latestMeta struct {
	Version string `json:"version"`
}

func readLatest(path string) (latestMeta, bool) {
	// #nosec G304 -- callers build path from the content root, the constant
	// "manifests" component and a game id that passed publicGameID or the
	// registry filter in loadGamesFromRegistry.
	b, err := os.ReadFile(path)
	if err != nil {
		return latestMeta{}, false
	}
	var m latestMeta
	if json.Unmarshal(b, &m) != nil || m.Version == "" {
		return latestMeta{}, false
	}
	return m, true
}

// servePublishedIndex reads a news index file and serves only the published
// entries.
//
// There is deliberately no "return the file as-is" fallback: it used to run
// whenever the index failed to parse OR simply had no items, and in the second
// case it handed the raw bytes — drafts included — to the public. A file that
// cannot be parsed cannot be filtered either, so the only safe answer is an
// empty list.
func servePublishedIndex(w http.ResponseWriter, path string) {
	empty := map[string]any{"items": []any{}}
	// #nosec G304 -- path is the content root plus constant components and, for
	// the per-game index, a game id already validated by publicGameID.
	b, err := os.ReadFile(path)
	if err != nil {
		writeJSON(w, empty)
		return
	}
	var idx struct {
		Items []map[string]any `json:"items"`
	}
	if json.Unmarshal(b, &idx) != nil {
		// #nosec G706 -- the base name is "index.json" or a directory component
		// built from a game id that publicGameID has already restricted to
		// [A-Za-z0-9_-]; there is nothing to inject with.
		log.Printf("news index %s: malformed json, serving empty list", filepath.Base(path))
		writeJSON(w, empty)
		return
	}
	out := make([]map[string]any, 0, len(idx.Items))
	for _, it := range idx.Items {
		// Include by default if "published" is missing.
		include := true
		if v, ok := it["published"]; ok {
			if bv, ok2 := v.(bool); ok2 {
				include = bv
			}
		}
		if include {
			out = append(out, it)
		}
	}
	writeJSON(w, map[string]any{"items": out})
}

func handleNewsIndex(w http.ResponseWriter, _ *http.Request) {
	servePublishedIndex(w, filepath.Join(contentRoot, "news", "index.json"))
}

// handleGameNewsIndex filters per-game news by published=true.
func handleGameNewsIndex(w http.ResponseWriter, r *http.Request) {
	gid, ok := publicGameID(w, r)
	if !ok {
		return
	}
	servePublishedIndex(w, filepath.Join(contentRoot, "news", "games", gid, "index.json"))
}

// handleBuilds returns list of available versions for a game by scanning
// manifests/{gameId}/ for *.json (excluding latest.json).
func handleBuilds(w http.ResponseWriter, r *http.Request) {
	gid, ok := publicGameID(w, r)
	if !ok {
		return
	}
	dir := filepath.Join(contentRoot, "manifests", gid)
	entries, err := os.ReadDir(dir)
	if err != nil {
		writeJSON(w, map[string]any{"gameId": gid, "items": []string{}})
		return
	}
	versions := make([]string, 0)
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		if strings.EqualFold(name, "latest.json") {
			continue
		}
		if strings.HasSuffix(strings.ToLower(name), ".json") {
			v := strings.TrimSuffix(name, ".json")
			versions = append(versions, v)
		}
	}
	// os.ReadDir returns names in lexicographic order, which is not version
	// order: "1.1.10" would come before "1.1.9". Clients take the list as
	// newest-first, so sort it semantically.
	adminutil.SortVersionsDesc(versions)
	writeJSON(w, map[string]any{"gameId": gid, "items": versions})
}

func baseURL(r *http.Request) string {
	proto := strings.TrimSpace(r.Header.Get("X-Forwarded-Proto"))
	if proto == "" {
		if r.TLS != nil {
			proto = "https"
		} else {
			proto = "http"
		}
	}
	host := strings.TrimSpace(r.Header.Get("X-Forwarded-Host"))
	if host == "" {
		host = r.Host
	}
	port := strings.TrimSpace(r.Header.Get("X-Forwarded-Port"))
	if port != "" && !strings.Contains(host, ":") {
		// The default port for the scheme is left off: "http://x:80" and
		// "https://x:443" are the same URL as "http://x" and "https://x", and
		// the launcher stores the base URL it is given.
		isDefaultPort := (proto == "http" && port == "80") || (proto == "https" && port == "443")
		if !isDefaultPort {
			host = host + ":" + port
		}
	}
	return proto + "://" + host
}

// writeJSON marshals before writing a single header: an encoder that failed
// mid-stream would have already sent 200 plus a truncated body, which a client
// reports as corrupt JSON instead of as the server error it is.
func writeJSON(w http.ResponseWriter, v any) {
	b, err := json.Marshal(v)
	if err != nil {
		log.Printf("encode response: %v", err)
		http.Error(w, "failed to encode response", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	_, _ = w.Write(b)
}

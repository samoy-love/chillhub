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

	"ChillHub/server/internal/httpx"
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
	// Determine content root
	contentRoot = os.Getenv("CONTENT_ROOT")
	if contentRoot == "" {
		// Fallback to ../../content relative to this file's directory at runtime
		exe, _ := os.Executable()
		base := filepath.Dir(exe)
		// try ../../../content (since exe is typically in server/cmd/api)
		try1 := filepath.Clean(filepath.Join(base, "..", "..", "..", "content"))
		if stat, err := os.Stat(try1); err == nil && stat.IsDir() {
			contentRoot = try1
		} else {
			// Try from current working directory and its parents
			cwd, _ := os.Getwd()
			candidates := []string{
				filepath.Join(cwd, "content"),
				filepath.Join(cwd, "..", "content"),
				filepath.Join(cwd, "..", "..", "content"),
			}
			found := false
			for _, c := range candidates {
				if stat, err := os.Stat(c); err == nil && stat.IsDir() {
					contentRoot = c
					found = true
					break
				}
			}
			if !found {
				contentRoot = "content" // last resort
			}
		}
	}

	limit, window := apiRateLimit()
	limiter := ratelimit.New(limit, window)

	r := mux.NewRouter()
	r.Use(httpx.RequestID())
	r.Use(httpx.CORS("*"))
	r.Use(limiter.Middleware)
	r.Use(httpx.Logging("PUBLIC"))
	r.HandleFunc("/api/games", handleGames).Methods("GET")
	r.HandleFunc("/api/games/{gameId}", handleGame).Methods("GET")
	r.HandleFunc("/api/games/{gameId}/versions/latest", handleLatest).Methods("GET")
	r.HandleFunc("/api/games/{gameId}/builds", handleBuilds).Methods("GET")
	r.HandleFunc("/news/index.json", handleNewsIndex).Methods("GET")
	r.HandleFunc("/news/games/{gameId}/index.json", handleGameNewsIndex).Methods("GET")

	// Serve manifests, content and news statically for local dev (no indirection)
	r.PathPrefix("/manifests/").Handler(httpx.NoStore(http.StripPrefix("/manifests/", http.FileServer(http.Dir(filepath.Join(contentRoot, "manifests"))))))
	r.PathPrefix("/content/").Handler(httpx.NoStore(http.StripPrefix("/content/", http.FileServer(http.Dir(filepath.Join(contentRoot, "content"))))))
	r.PathPrefix("/news/").Handler(httpx.NoStore(http.StripPrefix("/news/", http.FileServer(http.Dir(filepath.Join(contentRoot, "news"))))))
	r.PathPrefix("/news/games/").Handler(httpx.NoStore(http.StripPrefix("/news/games/", http.FileServer(http.Dir(filepath.Join(contentRoot, "news", "games"))))))
	r.PathPrefix("/assets/").Handler(httpx.NoStore(http.StripPrefix("/assets/", http.FileServer(http.Dir(filepath.Join(contentRoot, "news", "assets"))))))

	addr := ":55700"
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

var contentRoot string

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
	b, err := os.ReadFile(p)
	if err != nil {
		return nil, false
	}
	if json.Unmarshal(b, &reg) != nil || len(reg.Items) == 0 {
		return nil, false
	}
	out := make([]GameInfo, 0, len(reg.Items))
	for _, it := range reg.Items {
		out = append(out, GameInfo{GameID: it.GameID, Title: it.Title, ExeRelativePath: it.ExeRelativePath, IconURL: it.IconURL})
	}
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

func handleGame(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	gid := vars["gameId"]
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
	vars := mux.Vars(r)
	gid := vars["gameId"]
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

func handleNewsIndex(w http.ResponseWriter, r *http.Request) {
	path := filepath.Join(contentRoot, "news", "index.json")
	b, err := os.ReadFile(path)
	if err != nil {
		writeJSON(w, map[string]any{"items": []any{}})
		return
	}
	// filter by published
	var idx struct {
		Items []map[string]any `json:"items"`
	}
	if json.Unmarshal(b, &idx) == nil && len(idx.Items) > 0 {
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
		return
	}
	// fallback: return as-is
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// handleGameNewsIndex filters per-game news by published=true
func handleGameNewsIndex(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	gid := vars["gameId"]
	if gid == "" {
		writeJSON(w, map[string]any{"items": []any{}})
		return
	}
	path := filepath.Join(contentRoot, "news", "games", gid, "index.json")
	b, err := os.ReadFile(path)
	if err != nil {
		writeJSON(w, map[string]any{"items": []any{}})
		return
	}
	var idx struct {
		Items []map[string]any `json:"items"`
	}
	if json.Unmarshal(b, &idx) == nil && len(idx.Items) > 0 {
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
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// handleBuilds returns list of available versions for a game by scanning manifests/{gameId}/ for *.json (excluding latest.json)
func handleBuilds(w http.ResponseWriter, r *http.Request) {
	vars := mux.Vars(r)
	gid := vars["gameId"]
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
		if !(proto == "http" && port == "80") && !(proto == "https" && port == "443") {
			host = host + ":" + port
		}
	}
	return proto + "://" + host
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	enc := json.NewEncoder(w)
	_ = enc.Encode(v)
}

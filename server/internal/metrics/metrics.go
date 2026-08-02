// Package metrics is the deliberately minimal telemetry sink for the launcher
// (Backlog: "Метрики и алерты"). It is NOT a monitoring system: there is no
// time-series database, no exporter, no external dependency and no agent. It is
// an append-only file plus one aggregation query.
//
// # What is collected, and what is not
//
// COLLECTED (all of it supplied by the client, nothing derived from the
// connection):
//   - installId  — an opaque random identifier the CLIENT generates once and
//     stores locally. It identifies an installation, not a person: it is not
//     derived from hardware, MAC, disk serial, Windows SID, user name or
//     account. It exists only so "40 launches" can be told apart from
//     "40 launches by one machine in a retry loop".
//   - event      — one of the fixed kinds in the eventKinds list below.
//   - appVersion — launcher version, e.g. "1.4.0".
//   - os         — coarse OS string, e.g. "Windows 11 x64".
//   - gameId / version / result / durationMs / bytes / errorCode — context for
//     install and update events.
//   - ts         — the server's receive time (UTC). The client's clock is not
//     trusted and its own timestamp, if any, is discarded.
//
// NOT COLLECTED, and explicitly dropped if a client ever sends it: IP address
// (the request address is used for rate limiting and is never written to the
// file), user or account names, Windows/host name, e-mail, file system paths,
// install directories, hardware identifiers, screen or locale fingerprints,
// free-form log text. The accepted fields are an allowlist — anything else in
// the JSON body is discarded by the decoder, so a future client cannot leak a
// new field by accident.
//
// # Storage
//
// <contentRoot>/metrics/events.jsonl — one JSON object per line, appended.
//
// The feedback inbox rewrites its whole JSON array on every submit, which is
// why it needs a hard item cap (feedback.MaxItems / feedback.Prune). Metrics
// arrive far more often than user reports, so the same design would be
// quadratic. Append-only NDJSON makes an ingest O(1); the size ceiling is
// enforced by rotation instead of pruning:
//
//	events.jsonl   grows to at most MaxFileBytes, then becomes
//	events.1.jsonl (the previous generation, replacing the older one)
//
// Total disk use is therefore bounded by 2*MaxFileBytes, and aggregation reads
// the previous generation first so a rotation does not lose the running week.
package metrics

import (
	"bufio"
	"encoding/json"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
)

// Storage limits.
const (
	// MaxFileBytes is the size at which the active file is rotated. Two
	// generations are kept, so the metrics directory stays under 2x this.
	MaxFileBytes = 16 << 20 // 16 MiB
	// MaxBodyBytes caps a single submission. One event is a few hundred bytes.
	MaxBodyBytes = 8 << 10
	// MaxScanLineBytes caps a line read back during aggregation; longer lines
	// are skipped rather than blowing up the scanner.
	MaxScanLineBytes = 64 << 10
	// maxSummaryDays bounds how many day buckets one summary may produce.
	maxSummaryDays = 400
)

// Field length caps. Every string is clamped instead of rejected: losing a
// truncated OS string is better than losing the event.
const (
	maxInstallID = 64
	maxVersion   = 64
	maxOS        = 120
	maxGameID    = 80
	maxErrorCode = 120
)

// Numeric caps. The client supplies these values and nothing validated them
// upwards: a single event with durationMs = MaxInt64 (a bug, a stuck stopwatch
// or a hostile client) overflowed the running sums in Totals and turned the
// whole summary into nonsense — including negative numbers. Values are clamped
// rather than rejected, so a bogus number costs one wrong event, not the event.
const (
	// maxDurationMs is 24 hours: no install or update takes longer, and the
	// launcher would have been restarted by then anyway.
	maxDurationMs int64 = 24 * 60 * 60 * 1000
	// maxEventBytes is 1 TiB, far above any build.
	maxEventBytes int64 = 1 << 40
	// maxEventFiles bounds the file counters of one event. Ten million files is
	// orders of magnitude above the largest build and keeps a bogus value from
	// dominating the sums the same way durationMs once did.
	maxEventFiles int64 = 10_000_000
)

// eventKinds is the allowlist of event names. An unknown kind is rejected with
// 400 so a typo in the client shows up immediately instead of silently
// polluting the aggregate.
var eventKinds = map[string]bool{
	// the launcher process started
	"launcher_start": true,
	// a game was installed from scratch
	"game_install": true,
	// an installed game was updated to another build
	"game_update": true,
	// a game process was started
	"game_launch": true,
	// something failed; errorCode carries the classification
	"error": true,
	// the user asked to verify an installed game against the manifest;
	// hashMismatches carries how many files disagreed
	"integrity_check": true,
}

// results is the allowlist for the outcome of install/update/launch events.
var results = map[string]bool{"ok": true, "fail": true, "cancel": true}

// Event is one stored line. Field names are the wire contract with the client.
//
// FilesDownloaded/FilesTotal/FullBytes/HashMismatches were added later and are
// optional: an older launcher simply omits them, and every consumer treats 0 as
// "not reported" rather than as a real zero. They exist because Bytes alone
// cannot answer the question the launcher was built to answer — how much of the
// build the user did NOT have to download.
type Event struct {
	TS         string `json:"ts"`
	InstallID  string `json:"installId,omitempty"`
	Event      string `json:"event"`
	AppVersion string `json:"appVersion,omitempty"`
	OS         string `json:"os,omitempty"`
	GameID     string `json:"gameId,omitempty"`
	Version    string `json:"version,omitempty"`
	Result     string `json:"result,omitempty"`
	DurationMs int64  `json:"durationMs,omitempty"`
	Bytes      int64  `json:"bytes,omitempty"`
	ErrorCode  string `json:"errorCode,omitempty"`
	// FilesDownloaded is how many files the operation actually fetched.
	FilesDownloaded int64 `json:"filesDownloaded,omitempty"`
	// FilesTotal is how many files the build has in total.
	FilesTotal int64 `json:"filesTotal,omitempty"`
	// FullBytes is what the same operation would have weighed as a full download.
	FullBytes int64 `json:"fullBytes,omitempty"`
	// HashMismatches counts files whose hash disagreed with the manifest.
	HashMismatches int64 `json:"hashMismatches,omitempty"`
}

// Handlers serves the metrics endpoints for one content root.
type Handlers struct {
	root string
	mu   sync.Mutex
	// CurrentUser resolves the acting admin for audit log lines. It may be nil.
	CurrentUser func(*http.Request) string
	// Prom mirrors accepted events into Prometheus counters. It may be nil (the
	// tests and any process that does not export metrics leave it unset).
	Prom *Product
	// MaxBytes overrides MaxFileBytes; 0 means the constant. Only the tests set
	// it, so they can exercise rotation without writing 16 MiB.
	MaxBytes int64
}

// New returns handlers rooted at the given content directory.
func New(root string) *Handlers { return &Handlers{root: root} }

func (h *Handlers) maxBytes() int64 {
	if h.MaxBytes > 0 {
		return h.MaxBytes
	}
	return MaxFileBytes
}

func (h *Handlers) dir() string      { return filepath.Join(h.root, "metrics") }
func (h *Handlers) path() string     { return filepath.Join(h.dir(), "events.jsonl") }
func (h *Handlers) prevPath() string { return filepath.Join(h.dir(), "events.1.jsonl") }

func (h *Handlers) user(r *http.Request) string {
	if h.CurrentUser == nil {
		return ""
	}
	return h.CurrentUser(r)
}

// clampInt64 forces a client-supplied number into [0, max].
func clampInt64(v, max int64) int64 {
	if v < 0 {
		return 0
	}
	if v > max {
		return max
	}
	return v
}

func clamp(s string, n int) string {
	s = strings.TrimSpace(s)
	if len(s) > n {
		return s[:n]
	}
	return s
}

// Submit is the public endpoint (POST /metrics/report). It is unauthenticated
// and rate limited per client address by the caller, exactly like
// /feedback/submit.
func (h *Handlers) Submit(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodOptions {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	// The decoder target IS the allowlist: unknown JSON members are dropped, so
	// a client cannot smuggle extra data into the store.
	var in struct {
		InstallID       string `json:"installId"`
		Event           string `json:"event"`
		AppVersion      string `json:"appVersion"`
		OS              string `json:"os"`
		GameID          string `json:"gameId"`
		Version         string `json:"version"`
		Result          string `json:"result"`
		DurationMs      int64  `json:"durationMs"`
		Bytes           int64  `json:"bytes"`
		ErrorCode       string `json:"errorCode"`
		FilesDownloaded int64  `json:"filesDownloaded"`
		FilesTotal      int64  `json:"filesTotal"`
		FullBytes       int64  `json:"fullBytes"`
		HashMismatches  int64  `json:"hashMismatches"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, MaxBodyBytes)).Decode(&in); err != nil {
		h.Prom.Reject("bad_body")
		http.Error(w, "invalid json body", http.StatusBadRequest)
		return
	}
	kind := strings.ToLower(clamp(in.Event, 40))
	if !eventKinds[kind] {
		h.Prom.Reject("unknown_event")
		http.Error(w, "unknown event", http.StatusBadRequest)
		return
	}
	res := strings.ToLower(clamp(in.Result, 16))
	if res != "" && !results[res] {
		res = ""
	}
	in.DurationMs = clampInt64(in.DurationMs, maxDurationMs)
	in.Bytes = clampInt64(in.Bytes, maxEventBytes)
	in.FullBytes = clampInt64(in.FullBytes, maxEventBytes)
	in.FilesDownloaded = clampInt64(in.FilesDownloaded, maxEventFiles)
	in.FilesTotal = clampInt64(in.FilesTotal, maxEventFiles)
	in.HashMismatches = clampInt64(in.HashMismatches, maxEventFiles)
	ev := Event{
		// Server time on purpose: a wrong client clock would otherwise scatter
		// events across the day buckets.
		TS:         time.Now().UTC().Format(time.RFC3339),
		InstallID:  clamp(in.InstallID, maxInstallID),
		Event:      kind,
		AppVersion: clamp(in.AppVersion, maxVersion),
		OS:         clamp(in.OS, maxOS),
		GameID:     clamp(in.GameID, maxGameID),
		Version:    clamp(in.Version, maxVersion),
		Result:     res,
		DurationMs: in.DurationMs,
		Bytes:      in.Bytes,
		ErrorCode:  clamp(in.ErrorCode, maxErrorCode),

		FilesDownloaded: in.FilesDownloaded,
		FilesTotal:      in.FilesTotal,
		FullBytes:       in.FullBytes,
		HashMismatches:  in.HashMismatches,
	}
	if err := h.append(ev); err != nil {
		// This endpoint is public and unauthenticated: err.Error() would hand a
		// stranger the absolute content-root path the moment the disk fills up.
		log.Printf("[metrics] append: %v", err)
		h.Prom.Reject("store_failed")
		http.Error(w, "failed to store event", http.StatusInternalServerError)
		return
	}
	// Считаем только то, что действительно легло в файл: иначе график и сводка
	// админки разошлись бы ровно в тот момент, когда что-то сломалось, то есть
	// когда сверять их важнее всего.
	h.Prom.Record(ev)
	writeJSON(w, map[string]string{"status": "ok"})
}

// append writes one line, rotating first if the active file is at its ceiling.
func (h *Handlers) append(ev Event) error {
	line, err := json.Marshal(ev)
	if err != nil {
		return err
	}
	line = append(line, '\n')

	h.mu.Lock()
	defer h.mu.Unlock()
	if err := os.MkdirAll(h.dir(), 0o755); err != nil {
		return err
	}
	limit := h.maxBytes()
	if st, err := os.Stat(h.path()); err == nil && st.Size()+int64(len(line)) > limit {
		// Rotate: the previous generation is dropped, the active file takes its
		// place. On Windows Rename refuses to overwrite, hence the Remove.
		_ = os.Remove(h.prevPath())
		if err := os.Rename(h.path(), h.prevPath()); err != nil {
			// Rotation failing must not lose the event; truncate as a last resort
			// only if the file has grown past twice the ceiling.
			if st.Size() > 2*limit {
				_ = os.Remove(h.path())
			}
		}
	}
	f, err := os.OpenFile(h.path(), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
	if err != nil {
		return err
	}
	_, werr := f.Write(line)
	cerr := f.Close()
	if werr != nil {
		return werr
	}
	return cerr
}

// ===== aggregation (admin) =====

// DayBucket is one calendar day (UTC) of counts.
type DayBucket struct {
	Date           string `json:"date"`
	LauncherStarts int    `json:"launcherStarts"`
	Installs       int    `json:"installs"`
	Updates        int    `json:"updates"`
	GameLaunches   int    `json:"gameLaunches"`
	Errors         int    `json:"errors"`
}

// GameBucket is per-game activity within the requested period.
type GameBucket struct {
	GameID   string `json:"gameId"`
	Installs int    `json:"installs"`
	Updates  int    `json:"updates"`
	Errors   int    `json:"errors"`
	Bytes    int64  `json:"bytes"`
}

// CountBucket is a generic name/count pair (error codes, versions, OS strings).
type CountBucket struct {
	Key   string `json:"key"`
	Count int    `json:"count"`
}

// Totals is the headline set the admin panel shows.
type Totals struct {
	Events          int   `json:"events"`
	LauncherStarts  int   `json:"launcherStarts"`
	Installs        int   `json:"installs"`
	InstallOK       int   `json:"installOk"`
	InstallFail     int   `json:"installFail"`
	Updates         int   `json:"updates"`
	UpdateOK        int   `json:"updateOk"`
	UpdateFail      int   `json:"updateFail"`
	GameLaunches    int   `json:"gameLaunches"`
	Errors          int   `json:"errors"`
	UniqueInstalls  int   `json:"uniqueInstalls"`
	BytesDownloaded int64 `json:"bytesDownloaded"`
	// AvgInstallMs / AvgUpdateMs average only successful operations with a
	// reported duration; a cancelled download would otherwise drag them down.
	AvgInstallMs int64 `json:"avgInstallMs"`
	AvgUpdateMs  int64 `json:"avgUpdateMs"`
}

// Summary is the response of /admin/api/metrics/summary.
type Summary struct {
	From       string        `json:"from"`
	To         string        `json:"to"`
	Totals     Totals        `json:"totals"`
	ByDay      []DayBucket   `json:"byDay"`
	ByGame     []GameBucket  `json:"byGame"`
	TopErrors  []CountBucket `json:"topErrors"`
	AppVersion []CountBucket `json:"appVersions"`
	OS         []CountBucket `json:"os"`
	// DaysTruncated reports that the requested period held more distinct days
	// than maxSummaryDays: Totals and the per-game/error breakdowns still cover
	// every event, but ByDay does not.
	DaysTruncated bool `json:"daysTruncated,omitempty"`
}

// Summary serves GET /admin/api/metrics/summary?from=&to=&gameId=.
// from/to are RFC 3339; the default period is the last 30 days.
func (h *Handlers) Summary(w http.ResponseWriter, r *http.Request) {
	now := time.Now().UTC()
	from := now.AddDate(0, 0, -30)
	to := now
	if v := strings.TrimSpace(r.URL.Query().Get("from")); v != "" {
		t, err := time.Parse(time.RFC3339, v)
		if err != nil {
			http.Error(w, "from must be RFC3339", http.StatusBadRequest)
			return
		}
		from = t.UTC()
	}
	if v := strings.TrimSpace(r.URL.Query().Get("to")); v != "" {
		t, err := time.Parse(time.RFC3339, v)
		if err != nil {
			http.Error(w, "to must be RFC3339", http.StatusBadRequest)
			return
		}
		to = t.UTC()
	}
	gameFilter := strings.TrimSpace(r.URL.Query().Get("gameId"))

	out := Summary{
		From:      from.Format(time.RFC3339),
		To:        to.Format(time.RFC3339),
		ByDay:     []DayBucket{},
		ByGame:    []GameBucket{},
		TopErrors: []CountBucket{},
	}
	days := map[string]*DayBucket{}
	gamesAgg := map[string]*GameBucket{}
	errs := map[string]int{}
	appVers := map[string]int{}
	oses := map[string]int{}
	uniq := map[string]struct{}{}
	var installMsSum, updateMsSum int64
	var installMsN, updateMsN int64

	// The scan deliberately runs WITHOUT h.mu.
	//
	// Holding it for the whole pass — up to 32 MiB of NDJSON — blocked the
	// public /metrics/report endpoint (and the launchers behind it) for as long
	// as an admin's summary took. The store is append-only, so a concurrent
	// writer can only add whole lines at the end: a scan may or may not see an
	// event that arrives while it runs, and a rotation in the middle of a pass
	// can cost the rotated tail for that one summary. Both are acceptable for an
	// aggregate; blocking ingest is not.
	// Oldest generation first so byDay comes out chronological before sorting.
	files := []string{h.prevPath(), h.path()}
	var scanErr error
	for _, p := range files {
		if err := scanFile(p, func(ev Event) {
			t, err := time.Parse(time.RFC3339, ev.TS)
			if err != nil {
				return
			}
			t = t.UTC()
			if t.Before(from) || t.After(to) {
				return
			}
			if gameFilter != "" && ev.GameID != gameFilter {
				return
			}
			// Old lines were written before the numeric caps existed, so clamp
			// again here: one bogus value must not poison the totals.
			ev.DurationMs = clampInt64(ev.DurationMs, maxDurationMs)
			ev.Bytes = clampInt64(ev.Bytes, maxEventBytes)
			out.Totals.Events++
			if ev.InstallID != "" {
				uniq[ev.InstallID] = struct{}{}
			}
			if ev.AppVersion != "" {
				appVers[ev.AppVersion]++
			}
			if ev.OS != "" {
				oses[ev.OS]++
			}
			out.Totals.BytesDownloaded += ev.Bytes

			key := t.Format("2006-01-02")
			d := days[key]
			if d == nil {
				if len(days) >= maxSummaryDays {
					// The day cap must not skip the event: Totals were already
					// incremented above, so returning here made the headline
					// numbers disagree with the per-type breakdown below without
					// a word to the caller. The event is counted everywhere; only
					// its (capped) day bucket is dropped, and the response says
					// so.
					d = &DayBucket{Date: key}
					out.DaysTruncated = true
				} else {
					d = &DayBucket{Date: key}
					days[key] = d
				}
			}
			var g *GameBucket
			if ev.GameID != "" {
				g = gamesAgg[ev.GameID]
				if g == nil {
					g = &GameBucket{GameID: ev.GameID}
					gamesAgg[ev.GameID] = g
				}
				g.Bytes += ev.Bytes
			}

			switch ev.Event {
			case "launcher_start":
				out.Totals.LauncherStarts++
				d.LauncherStarts++
			case "game_install":
				out.Totals.Installs++
				d.Installs++
				if g != nil {
					g.Installs++
				}
				switch ev.Result {
				case "ok":
					out.Totals.InstallOK++
					if ev.DurationMs > 0 {
						installMsSum += ev.DurationMs
						installMsN++
					}
				case "fail":
					out.Totals.InstallFail++
				}
			case "game_update":
				out.Totals.Updates++
				d.Updates++
				if g != nil {
					g.Updates++
				}
				switch ev.Result {
				case "ok":
					out.Totals.UpdateOK++
					if ev.DurationMs > 0 {
						updateMsSum += ev.DurationMs
						updateMsN++
					}
				case "fail":
					out.Totals.UpdateFail++
				}
			case "game_launch":
				out.Totals.GameLaunches++
				d.GameLaunches++
			case "error":
				out.Totals.Errors++
				d.Errors++
				if g != nil {
					g.Errors++
				}
				code := ev.ErrorCode
				if code == "" {
					code = "unknown"
				}
				errs[code]++
			}
		}); err != nil {
			scanErr = err
		}
	}
	if scanErr != nil {
		log.Printf("[metrics] summary scan: %v", scanErr)
		http.Error(w, "failed to read metrics", http.StatusInternalServerError)
		return
	}

	out.Totals.UniqueInstalls = len(uniq)
	if installMsN > 0 {
		out.Totals.AvgInstallMs = installMsSum / installMsN
	}
	if updateMsN > 0 {
		out.Totals.AvgUpdateMs = updateMsSum / updateMsN
	}
	for _, d := range days {
		out.ByDay = append(out.ByDay, *d)
	}
	sort.Slice(out.ByDay, func(i, j int) bool { return out.ByDay[i].Date < out.ByDay[j].Date })
	for _, g := range gamesAgg {
		out.ByGame = append(out.ByGame, *g)
	}
	sort.Slice(out.ByGame, func(i, j int) bool {
		a, b := out.ByGame[i], out.ByGame[j]
		if a.Installs+a.Updates != b.Installs+b.Updates {
			return a.Installs+a.Updates > b.Installs+b.Updates
		}
		return a.GameID < b.GameID
	})
	out.TopErrors = topN(errs, 20)
	out.AppVersion = topN(appVers, 20)
	out.OS = topN(oses, 20)
	writeJSON(w, out)
}

// Clear serves POST /admin/api/metrics/clear: drops both generations.
func (h *Handlers) Clear(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodOptions {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	h.mu.Lock()
	err1 := os.Remove(h.path())
	err2 := os.Remove(h.prevPath())
	h.mu.Unlock()
	for _, err := range []error{err1, err2} {
		if err != nil && !os.IsNotExist(err) {
			log.Printf("[metrics] clear: %v", err)
			http.Error(w, "failed to clear metrics", http.StatusInternalServerError)
			return
		}
	}
	log.Printf("[audit] metrics clear by=%s", h.user(r))
	writeJSON(w, map[string]string{"status": "ok"})
}

func topN(m map[string]int, n int) []CountBucket {
	out := make([]CountBucket, 0, len(m))
	for k, v := range m {
		out = append(out, CountBucket{Key: k, Count: v})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Count != out[j].Count {
			return out[i].Count > out[j].Count
		}
		return out[i].Key < out[j].Key
	})
	if len(out) > n {
		out = out[:n]
	}
	return out
}

// scanFile streams one NDJSON generation. A missing file is not an error, and a
// malformed line is skipped: half a line at the tail (a crash mid-append) must
// not make the whole summary fail.
func scanFile(path string, fn func(Event)) error {
	f, err := os.Open(path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64<<10), MaxScanLineBytes)
	for sc.Scan() {
		line := sc.Bytes()
		if len(line) == 0 {
			continue
		}
		var ev Event
		if json.Unmarshal(line, &ev) != nil {
			continue
		}
		fn(ev)
	}
	// A scanner error (an over-long line, a truncated tail) ends this generation
	// early but must not fail the whole summary — partial numbers beat a 500.
	if err := sc.Err(); err != nil {
		log.Printf("[metrics] %s: %v (aggregation truncated)", path, err)
	}
	return nil
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	b, _ := json.Marshal(v)
	_, _ = w.Write(b)
}

// Package feedback stores user reports submitted from the launcher and serves
// the admin endpoints that browse them.
//
// A submission is appended to a small NDJSON journal (inbox.pending.ndjson);
// the inbox array (inbox.json) is rebuilt from journal + array only when the
// journal grows past journalCompactBytes or when an admin operation rewrites
// the inbox anyway. Rewriting the whole array on every public submit made the
// endpoint quadratic. Per-item and total size stay bounded by Prune, which runs
// at compaction.
package feedback

import (
	"bufio"
	"encoding/json"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"slices"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"ChillHub/server/internal/adminutil"
)

// Item is a single user report as stored in the inbox file.
type Item struct {
	ID         string            `json:"id"`
	CreatedAt  string            `json:"createdAt"`
	Type       string            `json:"type"` // one of: idea, question, bug, other
	Name       string            `json:"name"`
	Contact    string            `json:"contact"`
	Comment    string            `json:"comment"`
	Important  bool              `json:"important"`
	Status     string            `json:"status"` // one of: new, read, deleted
	AttachLogs bool              `json:"attachLogs"`
	Logs       string            `json:"logs,omitempty"`
	System     map[string]string `json:"system,omitempty"`

	// LogBytes is the size of Logs, filled in for responses that deliberately
	// omit the bundle itself. Without it the panel cannot tell "no logs
	// attached" from "logs are not in THIS response", and the download button
	// has nothing to show a size for.
	LogBytes int `json:"logBytes,omitempty"`
}

// Storage limits. The inbox is a single JSON file that is read and rewritten
// whole at every compaction, so both the per-item and the total size must stay
// bounded, otherwise the public submit endpoint degrades to O(n^2).
const (
	// MaxLogBytes is the max size of the diagnostics bundle accepted with a single report.
	//
	// Keep in step with Diagnostics.BundleMaxBytes in the launcher: the client trims
	// its bundle to that budget, and anything larger is clamped here. If the client
	// budget ever exceeds MaxBodyBytes below, reports do not get truncated — they are
	// rejected outright and lost.
	MaxLogBytes = 1 << 20 // 1 MiB
	// MaxItems is the max number of reports kept in the inbox.
	MaxItems = 2000
	// MaxTotalBytes is a soft budget for the whole inbox file.
	MaxTotalBytes = 64 << 20 // 64 MiB
	// MaxBodyBytes caps a single submission. /feedback/submit is public and
	// unauthenticated, so the decoder must not be handed an unbounded body —
	// metrics.Submit has done this from the start.
	//
	// The launcher caps its diagnostics bundle at MaxLogBytes (Diagnostics.cs,
	// BundleMaxBytes) and the server clamps it to the same; the budget here is
	// twice that plus room for the other fields, so JSON escaping of a log full
	// of newlines cannot push a legitimate report over the line.
	//
	// nginx must allow MORE than this for /feedback/submit, otherwise it rejects
	// the request first with a bare 413 instead of the JSON error from here.
	MaxBodyBytes = 2*MaxLogBytes + (128 << 10)
	// System is free-form key/value diagnostics from the client; every part of it
	// is clamped so one report cannot inflate the file that is rewritten at every
	// compaction.
	maxSystemEntries  = 40
	maxSystemKeyLen   = 64
	maxSystemValueLen = 512
	// The free-text fields of one report.
	maxNameLen    = 200
	maxContactLen = 200
	maxCommentLen = 5000

	// The inbox holds what users typed plus the diagnostics bundles their
	// launchers uploaded: names, contacts, log excerpts. It lives under the
	// content root, which nginx serves at /content/, so it must not be
	// world-readable — and it does not need to be group-readable either, since
	// only this service ever reads it back. The systemd units set UMask=0027
	// already; these constants make the code say the same thing.
	inboxDirPerm  os.FileMode = 0o750
	inboxFilePerm os.FileMode = 0o600
)

// clampSystem bounds the free-form diagnostics map: the number of entries and
// the length of every key and value.
func clampSystem(in map[string]string, clamp func(string, int) string) map[string]string {
	if len(in) == 0 {
		return nil
	}
	// Deterministic truncation: without sorting, which entries survive would
	// depend on Go's randomised map order.
	keys := make([]string, 0, len(in))
	for k := range in {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	if len(keys) > maxSystemEntries {
		keys = keys[:maxSystemEntries]
	}
	out := make(map[string]string, len(keys))
	for _, k := range keys {
		out[strings.TrimSpace(clamp(k, maxSystemKeyLen))] = clamp(in[k], maxSystemValueLen)
	}
	return out
}

// Handlers serves the feedback endpoints for one content root.
type Handlers struct {
	root string
	mu   sync.Mutex
	// CurrentUser resolves the acting admin for audit log lines. It may be nil.
	CurrentUser func(*http.Request) string

	// compacting keeps at most one background rewrite in flight: submissions
	// arrive faster than a 64 MiB rewrite finishes, and a goroutine per submit
	// would queue them all up on the same mutex.
	compacting atomic.Bool
	// compactWG accounts for that goroutine so a caller can wait for the
	// rewrite instead of observing the inbox halfway through it.
	compactWG sync.WaitGroup
}

// New returns handlers rooted at the given content directory.
func New(root string) *Handlers { return &Handlers{root: root} }

func (h *Handlers) dir() string  { return filepath.Join(h.root, "feedback") }
func (h *Handlers) path() string { return filepath.Join(h.dir(), "inbox.json") }

func (h *Handlers) user(r *http.Request) string {
	if h.CurrentUser == nil {
		return ""
	}
	return h.CurrentUser(r)
}

// journalPath is the append-only landing zone for new reports.
//
// /feedback/submit is public: rewriting the whole inbox (up to MaxTotalBytes,
// i.e. 64 MiB) under a global mutex on every single submission made the
// endpoint quadratic and let anyone keep the admin API busy with disk I/O. A
// submission now costs one appended line; the array file is rebuilt only when
// the journal has grown past journalCompactBytes — in the background, off the
// submitting request's own path — or whenever an admin operation rewrites the
// inbox anyway.
func (h *Handlers) journalPath() string { return filepath.Join(h.dir(), "inbox.pending.ndjson") }

// journalCompactBytes is how much unmerged journal is tolerated before a
// compaction is worth doing. It bounds both the extra memory a read costs and
// how far the inbox can overshoot Prune's limits between compactions.
//
// It has to stay well above MaxLogBytes. While the two were equal, a single
// report carrying the largest bundle the client is allowed to send already
// pushed the journal past the threshold, so the amortisation promised above
// never happened for exactly those reports: every such submission rewrote the
// whole inbox. Sixteen times the largest single report means a compaction is
// paid for by many submissions, which is what makes it amortised.
const journalCompactBytes = 16 * MaxLogBytes // 16 MiB

// startCompaction rebuilds the inbox array from journal + array in the
// background, unless a rebuild is already running.
//
// A failure is logged and nothing else: the reports are already durable in the
// journal, readAll merges them in regardless, and the next submission past the
// threshold tries again.
func (h *Handlers) startCompaction() {
	if !h.compacting.CompareAndSwap(false, true) {
		return
	}
	h.compactWG.Go(func() {
		defer h.compacting.Store(false)
		h.mu.Lock()
		defer h.mu.Unlock()
		items, err := h.readAll()
		if err == nil {
			err = h.writeAll(Prune(items))
		}
		if err != nil {
			log.Printf("[feedback] compact inbox: %v", err)
		}
	})
}

// waitCompaction blocks until a background compaction has finished.
func (h *Handlers) waitCompaction() { h.compactWG.Wait() }

// readAll returns the inbox, newest first: the compacted array plus everything
// still sitting in the journal.
func (h *Handlers) readAll() ([]Item, error) {
	var items []Item
	b, err := os.ReadFile(h.path())
	if err != nil {
		if !os.IsNotExist(err) {
			return nil, err
		}
	} else if json.Unmarshal(b, &items) != nil {
		items = nil
	}
	pending := h.readJournal()
	if len(pending) == 0 {
		if items == nil {
			return []Item{}, nil
		}
		return items, nil
	}
	// The journal is oldest-first; the inbox is newest-first.
	out := make([]Item, 0, len(items)+len(pending))
	for _, it := range slices.Backward(pending) {
		out = append(out, it)
	}
	return append(out, items...), nil
}

// readJournal returns the un-merged reports in arrival order. A truncated tail
// line (a crash mid-append) is skipped rather than failing the whole read.
func (h *Handlers) readJournal() []Item {
	f, err := os.Open(h.journalPath())
	if err != nil {
		return nil
	}
	defer func() { _ = f.Close() }()
	var out []Item
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64<<10), 2*MaxBodyBytes)
	for sc.Scan() {
		line := sc.Bytes()
		if len(line) == 0 {
			continue
		}
		var it Item
		if json.Unmarshal(line, &it) != nil {
			continue
		}
		out = append(out, it)
	}
	if err := sc.Err(); err != nil {
		log.Printf("[feedback] journal read: %v", err)
	}
	return out
}

// appendJournal stores one report with a single append and reports the journal
// size afterwards. Callers hold h.mu.
func (h *Handlers) appendJournal(it Item) (int64, error) {
	line, err := json.Marshal(it)
	if err != nil {
		return 0, err
	}
	line = append(line, '\n')
	if err := os.MkdirAll(h.dir(), inboxDirPerm); err != nil {
		return 0, err
	}
	f, err := os.OpenFile(h.journalPath(), os.O_CREATE|os.O_WRONLY|os.O_APPEND, inboxFilePerm)
	if err != nil {
		return 0, err
	}
	_, werr := f.Write(line)
	cerr := f.Close()
	if werr != nil {
		return 0, werr
	}
	if cerr != nil {
		return 0, cerr
	}
	// The report is already durably appended at this point. A failed Stat only
	// means we cannot tell whether the journal is now big enough to be worth
	// compacting, which is a decision that can safely wait for the next submit —
	// failing here instead would answer 500 for a report that WAS stored, and
	// the launcher would resend it.
	st, serr := os.Stat(h.journalPath())
	if serr != nil {
		log.Printf("[feedback] journal size unknown, compaction deferred: %v", serr)
		return 0, nil
	}
	return st.Size(), nil
}

func (h *Handlers) writeAll(items []Item) error {
	if err := os.MkdirAll(h.dir(), inboxDirPerm); err != nil {
		return err
	}
	// Item is a plain struct of strings, bools and a map[string]string, so this
	// cannot fail today — but writeAll is what replaces the whole inbox, and
	// truncating it to a nil buffer because a future field turned out to be
	// unmarshalable would silently destroy every report.
	b2, err := json.MarshalIndent(items, "", "  ")
	if err != nil {
		return err
	}
	dst := h.path()
	dir := filepath.Dir(dst)
	tmp, err := os.CreateTemp(dir, "inbox-*.json")
	if err != nil {
		return err
	}
	tmpName := tmp.Name()
	_, werr := tmp.Write(b2)
	cerr := tmp.Close()
	if werr != nil {
		_ = os.Remove(tmpName)
		return werr
	}
	if cerr != nil {
		_ = os.Remove(tmpName)
		return cerr
	}
	if runtime.GOOS == "windows" {
		_ = os.Remove(dst)
	}
	if err := os.Rename(tmpName, dst); err != nil {
		_ = os.Remove(tmpName)
		return err
	}
	// Everything the journal held is now part of the array file.
	if err := os.Remove(h.journalPath()); err != nil && !os.IsNotExist(err) {
		log.Printf("[feedback] remove journal: %v", err)
	}
	return nil
}

// itemSize is the JSON footprint of one item.
//
// It used to add up the raw field lengths, which undercounts by a lot for the
// data that actually fills the inbox: a log bundle full of newlines and quotes
// grows by 5 bytes per escaped character, so the file could pass MaxTotalBytes
// long before Prune thought it had. Marshalling is only paid during a
// compaction, not per submission.
func itemSize(it Item) int {
	if b, err := json.Marshal(it); err == nil {
		return len(b) + 4 // indentation and the separating comma
	}
	n := len(it.ID) + len(it.CreatedAt) + len(it.Type) + len(it.Name) +
		len(it.Contact) + len(it.Comment) + len(it.Logs) + 128
	for k, v := range it.System {
		n += len(k) + len(v) + 8
	}
	return n
}

// pruneToCount drops the oldest reports until at most MaxItems are left,
// preferring to keep the ones an admin flagged as important: pass one takes only
// non-important reports, and pass two goes back over the rest, still oldest
// first. Order is preserved for everything that survives.
func pruneToCount(items []Item) []Item {
	drop := len(items) - MaxItems
	if drop <= 0 {
		return items
	}
	removed := make([]bool, len(items))
	// pass 1: oldest non-important
	for i := len(items) - 1; i >= 0 && drop > 0; i-- {
		if !items[i].Important {
			removed[i] = true
			drop--
		}
	}
	// pass 2: everything else, oldest first
	for i := len(items) - 1; i >= 0 && drop > 0; i-- {
		if !removed[i] {
			removed[i] = true
			drop--
		}
	}
	kept := make([]Item, 0, MaxItems)
	for i, it := range items {
		if !removed[i] {
			kept = append(kept, it)
		}
	}
	return kept
}

// Prune enforces the count and total-size limits.
// items must be ordered newest-first (as stored). Oldest non-important reports
// are discarded first; if the size budget is still exceeded, log bundles of the
// oldest reports are dropped, and only then whole reports.
func Prune(items []Item) []Item {
	items = pruneToCount(items)

	total := 0
	for _, it := range items {
		total += itemSize(it)
	}
	// strip log bundles of the oldest reports first (metadata is preserved)
	for i := len(items) - 1; i >= 0 && total > MaxTotalBytes; i-- {
		if len(items[i].Logs) > 0 {
			total -= len(items[i].Logs)
			items[i].Logs = ""
			items[i].AttachLogs = false
		}
	}
	// last resort: drop the oldest reports entirely
	for len(items) > 1 && total > MaxTotalBytes {
		total -= itemSize(items[len(items)-1])
		items = items[:len(items)-1]
	}
	return items
}

// submission is the wire shape of a report as the launcher posts it. Every
// field is attacker-controlled — /feedback/submit is public — so nothing here
// reaches the inbox before toItem has bounded it.
type submission struct {
	Name       string            `json:"name"`
	Contact    string            `json:"contact"`
	Comment    string            `json:"comment"`
	Type       string            `json:"type"`
	AttachLogs bool              `json:"attachLogs"`
	Logs       string            `json:"logs"`
	System     map[string]string `json:"system"`
}

// clamp truncates s to at most n bytes.
func clamp(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n]
}

// normalizeType folds anything the client sends into the four known kinds, so
// the admin filter cannot be handed an unbounded set of values.
func normalizeType(v string) string {
	switch t := strings.ToLower(strings.TrimSpace(v)); t {
	case "bug", "idea", "question":
		return t
	default:
		return "other"
	}
}

// toItem turns a submission into the stored report, with every length bounded.
func (s submission) toItem() Item {
	return Item{
		ID:        adminutil.GenID(),
		CreatedAt: time.Now().UTC().Format(time.RFC3339),
		Type:      normalizeType(s.Type),
		Name:      strings.TrimSpace(clamp(s.Name, maxNameLen)),
		Contact:   strings.TrimSpace(clamp(s.Contact, maxContactLen)),
		// Kept as-is: the newlines and indentation the user typed are part of
		// what makes a bug report readable.
		Comment:   clamp(s.Comment, maxCommentLen),
		Important: false,
		Status:    "new",
		// Diagnostics bundles are capped: the whole inbox is rewritten on
		// compaction, so one report must not be able to inflate it.
		AttachLogs: s.AttachLogs,
		Logs:       clamp(s.Logs, MaxLogBytes),
		System:     clampSystem(s.System, clamp),
	}
}

// Submit is the public endpoint: accepts JSON or form; fields: name, contact,
// comment, type, attachLogs, logs, system.
func (h *Handlers) Submit(w http.ResponseWriter, r *http.Request) {
	// CORS for public endpoint
	if r.Method == http.MethodOptions {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	var in submission
	// Bound the body and REPORT a decode failure. Swallowing it stored a report
	// with every field empty, so a truncated or malformed submission looked to
	// the admin like a blank message from a user instead of an error.
	dec := json.NewDecoder(http.MaxBytesReader(w, r.Body, MaxBodyBytes))
	if err := dec.Decode(&in); err != nil {
		http.Error(w, "invalid json body", http.StatusBadRequest)
		return
	}
	item := in.toItem()
	// One appended line per submission; the inbox array is rebuilt only once the
	// journal is big enough to be worth it.
	h.mu.Lock()
	journalBytes, err := h.appendJournal(item)
	h.mu.Unlock()
	if err == nil && journalBytes > journalCompactBytes {
		// Not on the request's own path: the rewrite reads and writes the whole
		// inbox under the global mutex, and the sender of one report must not
		// be made to wait for it — nor should the admin's list of reports,
		// which takes the same mutex.
		h.startCompaction()
	}
	if err != nil {
		// Public endpoint: the error text would carry the content-root path.
		log.Printf("[feedback] store report: %v", err)
		http.Error(w, "failed to store report", http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]any{"status": "ok", "id": item.ID})
}

// List returns reports filtered by type, important(1/0), q (search in
// comment/contact/name), status, auto and a created-at range.
func (h *Handlers) List(w http.ResponseWriter, r *http.Request) {
	h.mu.Lock()
	items, _ := h.readAll()
	h.mu.Unlock()

	f := parseListFilters(r)
	out := make([]Item, 0, len(items))
	for _, it := range items {
		if !f.matches(it) {
			continue
		}

		// СПИСОК БЕЗ ЖУРНАЛОВ, И ЭТО НЕ ЭКОНОМИЯ РАДИ ЭКОНОМИИ.
		//
		// В списке до сотни обращений, у каждого до мегабайта диагностики, и
		// панель перезапрашивает список на каждое действие — прочитать,
		// пометить важным, удалить. Отдавать при этом сто мегабайт, из которых
		// UI показывает только имя и первую строку комментария, значит платить
		// временем оператора за данные, которые он не просил. Сам журнал
		// приходит по отдельному адресу, когда обращение открыли.
		it.LogBytes = len(it.Logs)
		it.Logs = ""
		out = append(out, it)
	}
	// Sort by CreatedAt desc
	sort.Slice(out, func(i, j int) bool { return out[i].CreatedAt > out[j].CreatedAt })
	adminutil.WriteJSON(w, struct {
		Items []Item `json:"items"`
	}{Items: out})
}

// listFilters is the parsed query of a List call. An empty field means "do not
// filter on this", which is why they are kept as strings rather than as typed
// zero values: "important=0" and no "important" at all are different requests.
type listFilters struct {
	kind     string // lower-cased Type
	status   string // lower-cased Status
	query    string // lower-cased substring of name/contact/comment
	imp      string // "" | truthy | falsy
	auto     string // "" | truthy | falsy
	from, to time.Time
}

// parseListFilters reads the filters off the query string. An unparseable date
// is treated as no bound at all: a typo in the admin UI must not silently hide
// every report.
func parseListFilters(r *http.Request) listFilters {
	q := r.URL.Query()
	f := listFilters{
		kind:   strings.ToLower(strings.TrimSpace(q.Get("type"))),
		status: strings.ToLower(strings.TrimSpace(q.Get("status"))),
		query:  strings.ToLower(strings.TrimSpace(q.Get("q"))),
		imp:    strings.TrimSpace(q.Get("important")),
		auto:   strings.TrimSpace(q.Get("auto")),
	}
	if t, err := time.Parse(time.RFC3339, strings.TrimSpace(q.Get("from"))); err == nil {
		f.from = t
	}
	if t, err := time.Parse(time.RFC3339, strings.TrimSpace(q.Get("to"))); err == nil {
		f.to = t
	}
	return f
}

// isTruthy reads the 1/0 and true/false spellings the admin UI sends.
func isTruthy(v string) bool { return v == "1" || strings.EqualFold(v, "true") }

// matches reports whether the report passes every active filter. Deleted reports
// never pass: Delete is a hard delete, but a report can also be soft-deleted by
// status and must not come back through the list.
func (f listFilters) matches(it Item) bool {
	if it.Status == "deleted" {
		return false
	}
	if f.kind != "" && strings.ToLower(it.Type) != f.kind {
		return false
	}
	if f.status != "" && strings.ToLower(it.Status) != f.status {
		return false
	}
	if f.imp != "" && it.Important != isTruthy(f.imp) {
		return false
	}
	if f.auto != "" && isTruthy(it.System["auto"]) != isTruthy(f.auto) {
		return false
	}
	if !f.inDateRange(it) {
		return false
	}
	if f.query != "" {
		hay := strings.ToLower(it.Name + "\n" + it.Contact + "\n" + it.Comment)
		if !strings.Contains(hay, f.query) {
			return false
		}
	}
	return true
}

// inDateRange applies the created-at bounds. A report whose CreatedAt does not
// parse is kept rather than dropped — losing a report because its timestamp is
// malformed would hide exactly the submissions worth looking at.
func (f listFilters) inDateRange(it Item) bool {
	if f.from.IsZero() && f.to.IsZero() {
		return true
	}
	t, err := time.Parse(time.RFC3339, it.CreatedAt)
	if err != nil {
		return true
	}
	if !f.from.IsZero() && t.Before(f.from) {
		return false
	}
	return f.to.IsZero() || !t.After(f.to)
}

// Get returns a single report by id.
func (h *Handlers) Get(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	items, _ := h.readAll()
	h.mu.Unlock()
	for _, it := range items {
		if it.ID == id {
			adminutil.WriteJSON(w, it)
			return
		}
	}
	http.Error(w, "not found", http.StatusNotFound)
}

// Logs returns the diagnostics bundle of one report as a plain text file.
//
// ОТДЕЛЬНЫМ АДРЕСОМ, А НЕ ПОЛЕМ В JSON. Бандл — до мегабайта текста, и внутри
// JSON он приезжает вместе со всем остальным: панель не может ни показать его
// частями, ни дать сохранить файлом, ни не тянуть его вовсе, пока оператор
// читает список. Здесь же он отдаётся ровно тем, чем является, — текстом, —
// и браузер сам предлагает его сохранить.
//
// Content-Disposition обязателен. Без него браузер показывает мегабайт лога
// прямо во вкладке, и «скачать» превращается в «выделить всё и скопировать».
func (h *Handlers) Logs(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	items, _ := h.readAll()
	h.mu.Unlock()

	for _, it := range items {
		if it.ID != id {
			continue
		}
		if it.Logs == "" {
			// Не 404: обращение существует, журнала у него нет. Разные ответы
			// на «нет такого обращения» и «журнал не прикладывали» — это
			// разница между «ошиблись ссылкой» и «смотреть нечего».
			http.Error(w, "no logs attached", http.StatusNoContent)
			return
		}

		// Имя файла собирается из идентификатора, а он проверен при приёме и
		// состоит из шестнадцатеричных цифр; всё равно экранируем — заголовок
		// уезжает в браузер, и одна кавычка в нём стоит дороже одной строки кода.
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		w.Header().Set("Content-Disposition", "attachment; filename="+strconv.Quote("feedback-"+safeFileID(id)+".log"))
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("Cache-Control", "no-store")
		if _, err := io.WriteString(w, it.Logs); err != nil {
			log.Printf("[feedback] logs id=%q: %v", id, err)
		}

		return
	}

	http.Error(w, "not found", http.StatusNotFound)
}

// safeFileID keeps only what a file name may contain. The id comes from our own
// generator, so this is a guard against a future change there rather than
// against the caller.
func safeFileID(id string) string {
	var b strings.Builder
	for _, r := range id {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9', r == '-', r == '_':
			b.WriteRune(r)
		default:
			b.WriteByte('_')
		}
	}
	if b.Len() == 0 {
		return "report"
	}
	return b.String()
}

// Delete hard-deletes a report.
func (h *Handlers) Delete(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	items, _ := h.readAll()
	out := make([]Item, 0, len(items))
	for _, it := range items {
		if it.ID == id {
			continue
		} // hard delete
		out = append(out, it)
	}
	err := h.writeAll(out)
	h.mu.Unlock()
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to update the inbox", "feedback", err)
		return
	}
	log.Printf("[audit] feedback delete id=%q by=%q", id, h.user(r))
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// ToggleImportant flips the important flag of a report.
func (h *Handlers) ToggleImportant(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	items, _ := h.readAll()
	changed := false
	newVal := false
	for i := range items {
		if items[i].ID == id {
			items[i].Important = !items[i].Important
			newVal = items[i].Important
			changed = true
			break
		}
	}
	if !changed {
		h.mu.Unlock()
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	err := h.writeAll(items)
	h.mu.Unlock()
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to update the inbox", "feedback", err)
		return
	}
	log.Printf("[audit] feedback important-toggle id=%q now=%v by=%q", id, newVal, h.user(r))
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// MarkRead sets a report's status to read.
func (h *Handlers) MarkRead(w http.ResponseWriter, r *http.Request) {
	h.setStatus(w, r, "read", "mark-read")
}

// MarkUnread reverts a report back to unread (status=new).
func (h *Handlers) MarkUnread(w http.ResponseWriter, r *http.Request) {
	h.setStatus(w, r, "new", "mark-unread")
}

func (h *Handlers) setStatus(w http.ResponseWriter, r *http.Request, status, audit string) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	items, _ := h.readAll()
	changed := false
	for i := range items {
		if items[i].ID == id {
			items[i].Status = status
			changed = true
			break
		}
	}
	if !changed {
		h.mu.Unlock()
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	err := h.writeAll(items)
	h.mu.Unlock()
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to update the inbox", "feedback", err)
		return
	}
	log.Printf("[audit] feedback %s id=%q by=%q", audit, id, h.user(r))
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// Clear empties the inbox.
func (h *Handlers) Clear(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	h.mu.Lock()
	err := h.writeAll([]Item{})
	h.mu.Unlock()
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to update the inbox", "feedback", err)
		return
	}
	log.Printf("[audit] feedback clear by=%q", h.user(r))
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

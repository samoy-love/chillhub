// Package maintenance implements the server-controlled maintenance mode
// (Backlog: "Режим технических работ").
//
// The whole feature is one small JSON file inside contentRoot:
//
//	<contentRoot>/maintenance/state.json
//
// It is stored exactly the way the games registry and the feedback inbox are —
// a plain file under contentRoot — so the admin panel manages it with the same
// deployment story (no database, nothing to migrate, `cat` shows the truth).
// A missing file simply means "maintenance is off"; that is not an error and
// never produces a non-200 response.
//
// Two audiences, two entry points:
//
//   - Public (launcher):  GET /api/maintenance          -> PublicHandler
//   - Admin:              GET/POST /admin/api/maintenance/{get,set,clear}
//
// The public endpoint is polled by every client at startup and periodically
// afterwards, so it must stay cheap. It is: the parsed state is cached in
// memory and only re-read when the file's mtime or size changes — or when the
// cache entry is older than cacheTTL — which makes a poll one os.Stat plus a
// small JSON encode. Nothing is allocated per game and nothing touches the
// manifests tree.
//
// AUTOMATIC RESET. The client is not asked to run a timer against a deadline;
// the server decides. Effective() compares startsAt/endsAt with the current
// time on every request, so a window that has expired reports enabled=false
// without anyone editing the file. That is what makes "the launcher returns to
// normal without a restart" work: the next poll simply says the mode is off.
package maintenance

import (
	"encoding/json"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"time"
)

// maxReasonBytes bounds the operator-supplied reason; it is displayed verbatim
// in a client banner, and the file is re-read on every cache miss.
const maxReasonBytes = 500

// cacheTTL is how long a parsed state may be reused without re-reading the
// file even when mtime and size are unchanged. Two seconds is invisible to an
// operator turning maintenance on and cheap for the poll rate the launcher uses.
const cacheTTL = 2 * time.Second

// Blocks says which client actions the mode forbids. All three are independent:
// a release can block installs while letting people keep playing what they
// already have.
type Blocks struct {
	// Install forbids installing a game that is not on disk yet.
	Install bool `json:"install"`
	// Update forbids updating an already installed game.
	Update bool `json:"update"`
	// Launch forbids starting an installed game.
	Launch bool `json:"launch"`
}

// Any reports whether at least one action is blocked. A state with no blocks is
// legal and means "show the banner, forbid nothing".
func (b Blocks) Any() bool { return b.Install || b.Update || b.Launch }

// State is both the on-disk format and the admin-facing representation.
//
// StartsAt/EndsAt are RFC 3339 timestamps (UTC recommended) or empty:
//   - empty StartsAt: the window is open from the moment it is enabled;
//   - empty EndsAt:   the window stays open until an operator turns it off.
type State struct {
	Enabled   bool   `json:"enabled"`
	Reason    string `json:"reason,omitempty"`
	StartsAt  string `json:"startsAt,omitempty"`
	EndsAt    string `json:"endsAt,omitempty"`
	Blocks    Blocks `json:"blocks"`
	UpdatedAt string `json:"updatedAt,omitempty"`
	UpdatedBy string `json:"updatedBy,omitempty"`
}

// Public is what the launcher receives. It deliberately omits UpdatedBy (an
// admin login name has no business leaving the admin API) and adds ServerTime
// so a client with a skewed clock can still render a sensible countdown.
type Public struct {
	Enabled bool   `json:"enabled"`
	Reason  string `json:"reason,omitempty"`
	// StartsAt is echoed back only while the window is active; a not-yet-started
	// window is reported as plain "off" so no banner is shown early.
	StartsAt   string `json:"startsAt,omitempty"`
	EndsAt     string `json:"endsAt,omitempty"`
	Blocks     Blocks `json:"blocks"`
	ServerTime string `json:"serverTime"`
}

// Store owns the state file for one content root.
type Store struct {
	root string

	mu sync.Mutex
	// cache of the last successful parse, invalidated by mtime/size
	cached    State
	cachedMod time.Time
	cachedLen int64
	cachedAt  time.Time
	valid     bool

	// CurrentUser resolves the acting admin for audit log lines. It may be nil.
	CurrentUser func(*http.Request) string
}

// New returns a store rooted at the given content directory.
func New(root string) *Store { return &Store{root: root} }

func (s *Store) dir() string  { return filepath.Join(s.root, "maintenance") }
func (s *Store) path() string { return filepath.Join(s.dir(), "state.json") }

func (s *Store) user(r *http.Request) string {
	if s.CurrentUser == nil {
		return ""
	}
	return s.CurrentUser(r)
}

// Load returns the stored state. A missing or unparsable file yields the zero
// State (maintenance off) and no error: the launcher must never be knocked into
// maintenance mode, or out of it, by a corrupt file.
func (s *Store) Load() State {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.loadLocked()
}

func (s *Store) loadLocked() State {
	st, err := os.Stat(s.path())
	if err != nil {
		s.valid = false
		s.cached = State{}
		return State{}
	}
	// mtime+size alone is not a reliable change signal: many filesystems store
	// mtime with coarse granularity, so two edits inside one tick that happen to
	// produce the same length (flipping a flag, swapping one character of the
	// reason) look identical — and the PUBLIC api process, which has its own
	// copy of this cache and never sees the admin write, would keep serving the
	// old state indefinitely. The TTL puts a hard ceiling on that staleness
	// while keeping a poll to one os.Stat in the common case.
	if s.valid && st.ModTime().Equal(s.cachedMod) && st.Size() == s.cachedLen &&
		time.Since(s.cachedAt) < cacheTTL {
		return s.cached
	}
	b, err := os.ReadFile(s.path())
	if err != nil {
		s.valid = false
		return State{}
	}
	var v State
	if json.Unmarshal(b, &v) != nil {
		s.valid = false
		return State{}
	}
	s.cached, s.cachedMod, s.cachedLen, s.valid = v, st.ModTime(), st.Size(), true
	s.cachedAt = time.Now()
	return v
}

// Effective collapses the stored state and the clock into what a client should
// actually do right now. Outside the window (or with enabled=false) everything
// is reported as off, blocks included — a stale endsAt can never leave clients
// stuck in maintenance.
func Effective(s State, now time.Time) Public {
	out := Public{ServerTime: now.UTC().Format(time.RFC3339)}
	if !s.Enabled {
		return out
	}
	if t, ok := parseTime(s.StartsAt); ok && now.Before(t) {
		return out // scheduled, but not started yet
	}
	if t, ok := parseTime(s.EndsAt); ok && !now.Before(t) {
		return out // window has expired: automatic reset, no admin action needed
	}
	out.Enabled = true
	out.Reason = s.Reason
	out.StartsAt = s.StartsAt
	out.EndsAt = s.EndsAt
	out.Blocks = s.Blocks
	return out
}

func parseTime(v string) (time.Time, bool) {
	v = strings.TrimSpace(v)
	if v == "" {
		return time.Time{}, false
	}
	t, err := time.Parse(time.RFC3339, v)
	if err != nil {
		return time.Time{}, false
	}
	return t, true
}

// Current is Effective(Load(), now) — the value the public endpoint serves.
func (s *Store) Current() Public { return Effective(s.Load(), time.Now()) }

// PublicHandler serves GET /api/maintenance. It always answers 200 with a JSON
// body; "off" is a normal answer, not a 404.
func (s *Store) PublicHandler(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodOptions {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	writeJSON(w, s.Current())
}

// ===== admin side =====

// AdminView is the admin representation: the raw stored record plus the
// effective one, so the panel can show "enabled, but the window ended an hour
// ago" without recomputing the rule.
type AdminView struct {
	State     State  `json:"state"`
	Effective Public `json:"effective"`
	// Path is the on-disk location, shown in the panel for support purposes.
	Path string `json:"path"`
}

// Get serves GET /admin/api/maintenance/get.
func (s *Store) Get(w http.ResponseWriter, _ *http.Request) {
	st := s.Load()
	writeJSON(w, AdminView{State: st, Effective: Effective(st, time.Now()), Path: s.path()})
}

// Set serves POST /admin/api/maintenance/set.
//
// The body is the full desired state; there is no partial update, because a
// half-applied maintenance window is worse than none. Timestamps must be
// RFC 3339 — a silently ignored malformed deadline would leave every client
// blocked forever, so a bad value is a 400.
func (s *Store) Set(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodOptions {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	var in State
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64<<10)).Decode(&in); err != nil {
		http.Error(w, "invalid json body", http.StatusBadRequest)
		return
	}
	in.Reason = strings.TrimSpace(in.Reason)
	if len(in.Reason) > maxReasonBytes {
		in.Reason = in.Reason[:maxReasonBytes]
	}
	if !normalizeWindow(w, &in) {
		return
	}
	now := time.Now()
	in.UpdatedAt = now.UTC().Format(time.RFC3339)
	in.UpdatedBy = s.user(r)

	if err := s.save(in); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	logAuditf("maintenance set enabled=%v blocks=%+v ends=%q by=%s", in.Enabled, in.Blocks, in.EndsAt, in.UpdatedBy)
	writeJSON(w, AdminView{State: in, Effective: Effective(in, now), Path: s.path()})
}

// normalizeWindow validates startsAt/endsAt and rewrites them in UTC so every
// consumer sees one format. It answers 400 itself and reports ok=false, so the
// caller only has to return.
//
// A malformed timestamp is a 400 rather than a silently ignored field: a
// deadline nobody stored leaves every client blocked forever.
func normalizeWindow(w http.ResponseWriter, in *State) bool {
	start, startOK := parseTime(in.StartsAt)
	if strings.TrimSpace(in.StartsAt) != "" && !startOK {
		http.Error(w, "startsAt must be RFC3339", http.StatusBadRequest)
		return false
	}
	end, endOK := parseTime(in.EndsAt)
	if strings.TrimSpace(in.EndsAt) != "" && !endOK {
		http.Error(w, "endsAt must be RFC3339", http.StatusBadRequest)
		return false
	}
	if startOK && endOK && !end.After(start) {
		http.Error(w, "endsAt must be after startsAt", http.StatusBadRequest)
		return false
	}
	if startOK {
		in.StartsAt = start.UTC().Format(time.RFC3339)
	}
	if endOK {
		in.EndsAt = end.UTC().Format(time.RFC3339)
	}
	return true
}

// Clear serves POST /admin/api/maintenance/clear: removes the state file, which
// is the canonical "off". Removing rather than writing enabled=false keeps the
// "no file = off" invariant the only thing anyone has to remember.
func (s *Store) Clear(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodOptions {
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	s.mu.Lock()
	err := os.Remove(s.path())
	s.cached, s.valid = State{}, false
	s.mu.Unlock()
	if err != nil && !os.IsNotExist(err) {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	logAuditf("maintenance clear by=%s", s.user(r))
	writeJSON(w, AdminView{Effective: Effective(State{}, time.Now()), Path: s.path()})
}

// save writes the state atomically (temp file + rename) so a reader never sees
// a half-written maintenance flag.
func (s *Store) save(v State) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	// 0o750, not the 0o755 of the content tree: this directory is never served,
	// only the two server processes read it.
	if err := os.MkdirAll(s.dir(), 0o750); err != nil {
		return err
	}
	b, err := json.MarshalIndent(v, "", "  ")
	if err != nil {
		return err
	}
	dst := s.path()
	tmp, err := os.CreateTemp(s.dir(), "state-*.json")
	if err != nil {
		return err
	}
	name := tmp.Name()
	_, werr := tmp.Write(b)
	cerr := tmp.Close()
	if werr != nil || cerr != nil {
		_ = os.Remove(name)
		if werr != nil {
			return werr
		}
		return cerr
	}
	if runtime.GOOS == "windows" {
		// Windows refuses to rename onto an existing file.
		_ = os.Remove(dst)
	}
	if err := os.Rename(name, dst); err != nil {
		_ = os.Remove(name)
		return err
	}
	s.valid = false // force a re-stat on the next read
	return nil
}

// logAuditf mirrors the "[audit] ..." lines the other admin domains emit.
func logAuditf(format string, a ...any) { log.Printf("[audit] "+format, a...) }

// writeJSON marshals before writing a single header: an encoder that failed
// mid-stream would have already sent 200 plus a truncated body, which a client
// reports as corrupt JSON instead of as the server error it is.
func writeJSON(w http.ResponseWriter, v any) {
	b, err := json.Marshal(v)
	if err != nil {
		log.Printf("[maintenance] encode response: %v", err)
		http.Error(w, "failed to encode response", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	_, _ = w.Write(b)
}

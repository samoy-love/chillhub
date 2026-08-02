package maintenance

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func decodePublic(t *testing.T, w *httptest.ResponseRecorder) Public {
	t.Helper()
	var p Public
	if err := json.Unmarshal(w.Body.Bytes(), &p); err != nil {
		t.Fatalf("bad json %q: %v", w.Body.String(), err)
	}
	return p
}

// A missing state file must read as "off" and never as an error.
func TestMissingFileIsDisabled(t *testing.T) {
	s := New(t.TempDir())
	w := httptest.NewRecorder()
	s.PublicHandler(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://x/api/maintenance", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("code = %d, want 200", w.Code)
	}
	p := decodePublic(t, w)
	if p.Enabled || p.Blocks.Any() {
		t.Fatalf("expected disabled, got %+v", p)
	}
	if p.ServerTime == "" {
		t.Fatal("serverTime missing")
	}
}

// A corrupt file must also read as "off" — clients must never be stranded.
func TestCorruptFileIsDisabled(t *testing.T) {
	root := t.TempDir()
	s := New(root)
	if err := os.MkdirAll(s.dir(), 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(s.path(), []byte("{not json"), 0o600); err != nil {
		t.Fatal(err)
	}
	if s.Current().Enabled {
		t.Fatal("corrupt file must not enable maintenance")
	}
}

func setState(t *testing.T, s *Store, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://x/admin/api/maintenance/set", strings.NewReader(body))
	w := httptest.NewRecorder()
	s.Set(w, req)
	return w
}

func TestSetEnablesAndPublicReflectsIt(t *testing.T) {
	s := New(t.TempDir())
	end := time.Now().Add(time.Hour).UTC().Format(time.RFC3339)
	w := setState(t, s, `{"enabled":true,"reason":"disk swap","endsAt":"`+end+`","blocks":{"install":true,"update":true,"launch":false}}`)
	if w.Code != http.StatusOK {
		t.Fatalf("set failed: %d %s", w.Code, w.Body.String())
	}
	pw := httptest.NewRecorder()
	s.PublicHandler(pw, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://x/api/maintenance", nil))
	p := decodePublic(t, pw)
	if !p.Enabled || p.Reason != "disk swap" {
		t.Fatalf("unexpected public state: %+v", p)
	}
	if !p.Blocks.Install || !p.Blocks.Update || p.Blocks.Launch {
		t.Fatalf("blocks not carried through: %+v", p.Blocks)
	}
	// UpdatedBy must not leak to the public payload.
	if strings.Contains(pw.Body.String(), "updatedBy") {
		t.Fatal("updatedBy leaked into the public response")
	}
}

// The whole point of the auto-reset: an expired window reports off without any
// admin action, and reports nothing blocked.
func TestExpiredWindowAutoResets(t *testing.T) {
	past := time.Now().Add(-time.Hour).UTC().Format(time.RFC3339)
	st := State{Enabled: true, Reason: "x", EndsAt: past, Blocks: Blocks{Install: true, Update: true, Launch: true}}
	p := Effective(st, time.Now())
	if p.Enabled || p.Blocks.Any() || p.Reason != "" {
		t.Fatalf("expired window must be fully off, got %+v", p)
	}
}

// A window scheduled for later must not switch clients into maintenance early.
func TestFutureWindowNotActiveYet(t *testing.T) {
	future := time.Now().Add(time.Hour).UTC().Format(time.RFC3339)
	st := State{Enabled: true, StartsAt: future, Blocks: Blocks{Install: true}}
	if Effective(st, time.Now()).Enabled {
		t.Fatal("future window must not be active")
	}
	// ...and it must become active once inside the window.
	if !Effective(st, time.Now().Add(2*time.Hour)).Enabled {
		t.Fatal("window must activate after startsAt")
	}
}

func TestSetRejectsBadTimestamps(t *testing.T) {
	s := New(t.TempDir())
	cases := []string{
		`{"enabled":true,"endsAt":"tomorrow"}`,
		`{"enabled":true,"startsAt":"nope"}`,
		`{"enabled":true,"startsAt":"2030-01-02T00:00:00Z","endsAt":"2030-01-01T00:00:00Z"}`,
	}
	for _, body := range cases {
		if got := setState(t, s, body).Code; got != http.StatusBadRequest {
			t.Errorf("body %s: code = %d, want 400", body, got)
		}
	}
}

func TestClearRemovesFile(t *testing.T) {
	s := New(t.TempDir())
	setState(t, s, `{"enabled":true,"blocks":{"install":true}}`)
	if !s.Current().Enabled {
		t.Fatal("precondition failed: not enabled")
	}
	w := httptest.NewRecorder()
	s.Clear(w, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://x/admin/api/maintenance/clear", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("clear failed: %d", w.Code)
	}
	if _, err := os.Stat(s.path()); !os.IsNotExist(err) {
		t.Fatalf("state file still present: %v", err)
	}
	if s.Current().Enabled {
		t.Fatal("still enabled after clear")
	}
	// Clearing twice is not an error.
	w2 := httptest.NewRecorder()
	s.Clear(w2, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://x/admin/api/maintenance/clear", nil))
	if w2.Code != http.StatusOK {
		t.Fatalf("second clear failed: %d", w2.Code)
	}
}

// The cache must not hide an edit made through Set.
func TestCacheInvalidatedOnWrite(t *testing.T) {
	s := New(t.TempDir())
	if s.Current().Enabled {
		t.Fatal("should start disabled")
	}
	setState(t, s, `{"enabled":true,"blocks":{"launch":true}}`)
	if !s.Current().Blocks.Launch {
		t.Fatal("cache served a stale disabled state")
	}
	setState(t, s, `{"enabled":false}`)
	if s.Current().Enabled {
		t.Fatal("cache served a stale enabled state")
	}
}

// A second process (the public API has its own Store) never sees the admin
// write, and mtime+size cannot tell apart two edits of the same length inside
// one filesystem timestamp tick. The TTL bounds how long such a change can stay
// invisible.
func TestCacheExpiresEvenWhenStatLooksUnchanged(t *testing.T) {
	dir := t.TempDir()
	s := New(dir)
	path := filepath.Join(dir, "maintenance", "state.json")
	if err := os.MkdirAll(filepath.Dir(path), 0o750); err != nil {
		t.Fatal(err)
	}
	// Two states of exactly the same length, written with the same mtime.
	a := []byte(`{"enabled":true, "reason":"aa"}`)
	b := []byte(`{"enabled":false,"reason":"aa"}`)
	if len(a) != len(b) {
		t.Fatalf("test states must be the same length: %d vs %d", len(a), len(b))
	}
	stamp := time.Now().Add(-time.Hour)
	write := func(data []byte) {
		if err := os.WriteFile(path, data, 0o600); err != nil {
			t.Fatal(err)
		}
		if err := os.Chtimes(path, stamp, stamp); err != nil {
			t.Fatal(err)
		}
	}
	write(a)
	if !s.Load().Enabled {
		t.Fatal("first read should see enabled=true")
	}
	write(b)
	// Simulate the TTL having elapsed rather than sleeping for it.
	s.mu.Lock()
	s.cachedAt = time.Now().Add(-2 * cacheTTL)
	s.mu.Unlock()
	if s.Load().Enabled {
		t.Fatal("the cache kept serving a stale state that stat could not distinguish")
	}
}

func TestReasonIsClamped(t *testing.T) {
	s := New(t.TempDir())
	long := strings.Repeat("x", maxReasonBytes+500)
	setState(t, s, `{"enabled":true,"reason":"`+long+`"}`)
	if got := len(s.Load().Reason); got != maxReasonBytes {
		t.Fatalf("reason length = %d, want %d", got, maxReasonBytes)
	}
}

// writeJSON must never emit 200 plus a truncated body: a value that cannot be
// encoded is a server error, and the client has to be able to tell.
func TestWriteJSONReportsEncodingFailure(t *testing.T) {
	w := httptest.NewRecorder()
	writeJSON(w, map[string]any{"bad": make(chan int)})
	if w.Code != http.StatusInternalServerError {
		t.Fatalf("code = %d, want 500 (body %q)", w.Code, w.Body.String())
	}
}

// Get is the admin read side: it must report both the stored record and the
// effective one, so the panel can show "enabled, but the window ended".
func TestAdminGetReportsStoredAndEffectiveState(t *testing.T) {
	s := New(t.TempDir())
	past := time.Now().Add(-time.Hour).UTC().Format(time.RFC3339)
	setState(t, s, `{"enabled":true,"reason":"истёкшее окно","endsAt":"`+past+`"}`)

	w := httptest.NewRecorder()
	s.Get(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://x/admin/api/maintenance/get", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("code = %d", w.Code)
	}
	var v AdminView
	if err := json.Unmarshal(w.Body.Bytes(), &v); err != nil {
		t.Fatalf("bad json %q: %v", w.Body.String(), err)
	}
	if !v.State.Enabled {
		t.Fatalf("stored state lost enabled=true: %+v", v.State)
	}
	if v.Effective.Enabled {
		t.Fatalf("an expired window must be reported as off: %+v", v.Effective)
	}
	if v.Path == "" {
		t.Fatal("path is not reported")
	}
}

// The public endpoint answers GET and HEAD and refuses everything else.
func TestPublicHandlerMethods(t *testing.T) {
	s := New(t.TempDir())
	for method, want := range map[string]int{
		http.MethodGet:     http.StatusOK,
		http.MethodHead:    http.StatusOK,
		http.MethodOptions: http.StatusNoContent,
		http.MethodPost:    http.StatusMethodNotAllowed,
	} {
		w := httptest.NewRecorder()
		s.PublicHandler(w, httptest.NewRequestWithContext(t.Context(), method, "http://x/api/maintenance", nil))
		if w.Code != want {
			t.Errorf("%s = %d, want %d", method, w.Code, want)
		}
	}
}

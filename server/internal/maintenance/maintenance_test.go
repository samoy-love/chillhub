package maintenance

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
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
	s.PublicHandler(w, httptest.NewRequest(http.MethodGet, "http://x/api/maintenance", nil))
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
	if err := os.MkdirAll(s.dir(), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(s.path(), []byte("{not json"), 0o644); err != nil {
		t.Fatal(err)
	}
	if s.Current().Enabled {
		t.Fatal("corrupt file must not enable maintenance")
	}
}

func setState(t *testing.T, s *Store, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequest(http.MethodPost, "http://x/admin/api/maintenance/set", strings.NewReader(body))
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
	s.PublicHandler(pw, httptest.NewRequest(http.MethodGet, "http://x/api/maintenance", nil))
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
	s.Clear(w, httptest.NewRequest(http.MethodPost, "http://x/admin/api/maintenance/clear", nil))
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
	s.Clear(w2, httptest.NewRequest(http.MethodPost, "http://x/admin/api/maintenance/clear", nil))
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

func TestReasonIsClamped(t *testing.T) {
	s := New(t.TempDir())
	long := strings.Repeat("x", maxReasonBytes+500)
	setState(t, s, `{"enabled":true,"reason":"`+long+`"}`)
	if got := len(s.Load().Reason); got != maxReasonBytes {
		t.Fatalf("reason length = %d, want %d", got, maxReasonBytes)
	}
}

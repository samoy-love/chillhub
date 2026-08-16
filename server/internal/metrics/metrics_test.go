package metrics

import (
	"encoding/json"
	"fmt"
	"math"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"
)

func submit(t *testing.T, h *Handlers, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://x/metrics/report", strings.NewReader(body))
	w := httptest.NewRecorder()
	h.Submit(w, req)
	return w
}

func summary(t *testing.T, h *Handlers, query string) Summary {
	t.Helper()
	req := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://x/admin/api/metrics/summary"+query, nil)
	w := httptest.NewRecorder()
	h.Summary(w, req)
	if w.Code != http.StatusOK {
		t.Fatalf("summary code = %d: %s", w.Code, w.Body.String())
	}
	var s Summary
	if err := json.Unmarshal(w.Body.Bytes(), &s); err != nil {
		t.Fatalf("bad json %q: %v", w.Body.String(), err)
	}
	return s
}

func TestSubmitAndAggregate(t *testing.T) {
	h := New(t.TempDir())
	bodies := []string{
		`{"installId":"aaa","event":"launcher_start","appVersion":"1.4.0","os":"Windows 11 x64"}`,
		`{"installId":"bbb","event":"launcher_start","appVersion":"1.4.0","os":"Windows 10 x64"}`,
		`{"installId":"aaa","event":"game_install","gameId":"g1","version":"1.0.0","result":"ok","durationMs":1000,"bytes":500}`,
		`{"installId":"aaa","event":"game_update","gameId":"g1","version":"1.0.1","result":"fail","durationMs":50}`,
		`{"installId":"bbb","event":"error","gameId":"g1","errorCode":"SYNC_HASH_MISMATCH"}`,
	}
	for _, b := range bodies {
		if w := submit(t, h, b); w.Code != http.StatusOK {
			t.Fatalf("submit %s -> %d %s", b, w.Code, w.Body.String())
		}
	}

	s := summary(t, h, "")
	if s.Totals.Events != 5 {
		t.Errorf("events = %d, want 5", s.Totals.Events)
	}
	if s.Totals.LauncherStarts != 2 {
		t.Errorf("launcherStarts = %d, want 2", s.Totals.LauncherStarts)
	}
	if s.Totals.Installs != 1 || s.Totals.InstallOK != 1 {
		t.Errorf("installs = %d/%d, want 1/1", s.Totals.Installs, s.Totals.InstallOK)
	}
	if s.Totals.Updates != 1 || s.Totals.UpdateFail != 1 {
		t.Errorf("updates = %d/%d fail, want 1/1", s.Totals.Updates, s.Totals.UpdateFail)
	}
	if s.Totals.Errors != 1 {
		t.Errorf("errors = %d, want 1", s.Totals.Errors)
	}
	if s.Totals.UniqueInstalls != 2 {
		t.Errorf("uniqueInstalls = %d, want 2", s.Totals.UniqueInstalls)
	}
	if s.Totals.BytesDownloaded != 500 {
		t.Errorf("bytes = %d, want 500", s.Totals.BytesDownloaded)
	}
	if s.Totals.AvgInstallMs != 1000 {
		t.Errorf("avgInstallMs = %d, want 1000", s.Totals.AvgInstallMs)
	}
	if len(s.ByDay) != 1 {
		t.Errorf("byDay buckets = %d, want 1", len(s.ByDay))
	}
	if len(s.ByGame) != 1 || s.ByGame[0].GameID != "g1" {
		t.Fatalf("byGame = %+v", s.ByGame)
	}
	if len(s.TopErrors) != 1 || s.TopErrors[0].Key != "SYNC_HASH_MISMATCH" {
		t.Fatalf("topErrors = %+v", s.TopErrors)
	}
}

func TestSubmitRejectsUnknownEvent(t *testing.T) {
	h := New(t.TempDir())
	if got := submit(t, h, `{"event":"mine_bitcoin"}`).Code; got != http.StatusBadRequest {
		t.Fatalf("code = %d, want 400", got)
	}
	if got := submit(t, h, `not json`).Code; got != http.StatusBadRequest {
		t.Fatalf("code = %d, want 400", got)
	}
}

// Unknown JSON members must be dropped: the stored record is an allowlist, so a
// future client cannot leak personal data by adding a field.
func TestUnknownFieldsAreNotStored(t *testing.T) {
	h := New(t.TempDir())
	submit(t, h, `{"event":"launcher_start","userName":"alexey","hostName":"DESKTOP-1","path":"C:\\Users\\alexey","ip":"1.2.3.4"}`)
	b, err := os.ReadFile(h.path())
	if err != nil {
		t.Fatal(err)
	}
	for _, needle := range []string{"alexey", "DESKTOP-1", "userName", "hostName", "1.2.3.4"} {
		if strings.Contains(string(b), needle) {
			t.Fatalf("stored line leaked %q: %s", needle, b)
		}
	}
}

// The client's address is used for rate limiting only; it must never be stored.
func TestClientAddressNotStored(t *testing.T) {
	h := New(t.TempDir())
	req := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://x/metrics/report", strings.NewReader(`{"event":"launcher_start"}`))
	req.RemoteAddr = "203.0.113.9:5555"
	req.Header.Set("X-Forwarded-For", "198.51.100.7")
	w := httptest.NewRecorder()
	h.Submit(w, req)
	b, _ := os.ReadFile(h.path())
	if strings.Contains(string(b), "203.0.113.9") || strings.Contains(string(b), "198.51.100.7") {
		t.Fatalf("client address stored: %s", b)
	}
}

func TestSummaryFiltersByPeriodAndGame(t *testing.T) {
	h := New(t.TempDir())
	submit(t, h, `{"event":"game_install","gameId":"g1","result":"ok"}`)
	submit(t, h, `{"event":"game_install","gameId":"g2","result":"ok"}`)

	byGame := summary(t, h, "?gameId=g1")
	if byGame.Totals.Installs != 1 {
		t.Errorf("gameId filter: installs = %d, want 1", byGame.Totals.Installs)
	}
	// A period entirely in the past must be empty.
	from := time.Now().AddDate(0, 0, -10).UTC().Format(time.RFC3339)
	to := time.Now().AddDate(0, 0, -9).UTC().Format(time.RFC3339)
	old := summary(t, h, "?from="+from+"&to="+to)
	if old.Totals.Events != 0 {
		t.Errorf("stale period: events = %d, want 0", old.Totals.Events)
	}
	// Bad timestamps are rejected loudly.
	req := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://x/admin/api/metrics/summary?from=yesterday", nil)
	w := httptest.NewRecorder()
	h.Summary(w, req)
	if w.Code != http.StatusBadRequest {
		t.Errorf("from=yesterday: code = %d, want 400", w.Code)
	}
}

// Aggregation must survive a truncated tail line (a crash mid-append).
func TestSummarySkipsCorruptLines(t *testing.T) {
	h := New(t.TempDir())
	submit(t, h, `{"event":"launcher_start"}`)
	f, err := os.OpenFile(h.path(), os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		t.Fatal(err)
	}
	_, _ = f.WriteString("{\"event\":\"launcher_st\n")
	_ = f.Close()
	if got := summary(t, h, "").Totals.Events; got != 1 {
		t.Fatalf("events = %d, want 1", got)
	}
}

// Rotation must bound disk use and must not lose the previous generation from
// the aggregate.
func TestRotationKeepsTwoGenerations(t *testing.T) {
	h := New(t.TempDir())
	h.MaxBytes = 4 << 10 // shrink the ceiling instead of writing 16 MiB
	// Fill the active file past the ceiling with a padded field.
	pad := strings.Repeat("e", maxErrorCode)
	line := fmt.Sprintf(`{"event":"error","errorCode":%q}`, pad)
	n := 0
	for {
		submit(t, h, line)
		n++
		st, err := os.Stat(h.prevPath())
		if err == nil && st.Size() > 0 {
			break
		}
		if n > 1000 {
			t.Fatal("rotation never happened")
		}
	}
	if _, err := os.Stat(h.path()); err != nil {
		t.Fatalf("active file missing after rotation: %v", err)
	}
	// Both generations are aggregated, so nothing was lost.
	if got := summary(t, h, "").Totals.Events; got != n {
		t.Fatalf("events after rotation = %d, want %d", got, n)
	}
}

// Numbers come from the client and nothing bounded them upwards: a single
// durationMs/bytes of MaxInt64 overflowed the running sums in Totals into
// negative values and made the whole summary nonsense.
func TestSubmitClampsNumericFields(t *testing.T) {
	h := New(t.TempDir())
	body := fmt.Sprintf(`{"event":"game_install","gameId":"g","result":"ok","durationMs":%d,"bytes":%d}`,
		int64(math.MaxInt64), int64(math.MaxInt64))
	for i := range 3 {
		if w := submit(t, h, body); w.Code != http.StatusOK {
			t.Fatalf("submit %d: %d %s", i, w.Code, w.Body.String())
		}
	}
	s := summary(t, h, "")
	if s.Totals.BytesDownloaded <= 0 {
		t.Fatalf("bytesDownloaded overflowed: %d", s.Totals.BytesDownloaded)
	}
	if s.Totals.AvgInstallMs <= 0 || s.Totals.AvgInstallMs > maxDurationMs {
		t.Fatalf("avgInstallMs out of range: %d", s.Totals.AvgInstallMs)
	}
	if s.ByGame[0].Bytes <= 0 {
		t.Fatalf("per-game bytes overflowed: %d", s.ByGame[0].Bytes)
	}
}

// sessionFixture submits five game_session events across two games and two
// installs, shared by the assertions below so each stays a small function on
// its own (golangci-lint's cyclop check counts branches per function, and one
// test asserting every field at once tripped its ceiling).
func sessionFixture(t *testing.T) Summary {
	t.Helper()
	h := New(t.TempDir())
	bodies := []string{
		`{"installId":"aaa","event":"game_session","gameId":"g1","durationMs":1000}`,
		`{"installId":"aaa","event":"game_session","gameId":"g1","durationMs":3000}`,
		`{"installId":"bbb","event":"game_session","gameId":"g1","durationMs":5000}`,
		`{"installId":"bbb","event":"game_session","gameId":"g2","durationMs":2000}`,
		// installId repeated across games must not inflate the per-game count.
		`{"installId":"bbb","event":"game_session","gameId":"g2","durationMs":4000}`,
	}
	for _, b := range bodies {
		if w := submit(t, h, b); w.Code != http.StatusOK {
			t.Fatalf("submit %s -> %d %s", b, w.Code, w.Body.String())
		}
	}
	return summary(t, h, "")
}

func TestSessionAggregatesPlaytime(t *testing.T) {
	t.Run("totals", func(t *testing.T) {
		s := sessionFixture(t)
		if s.Totals.GameSessions != 5 {
			t.Errorf("gameSessions = %d, want 5", s.Totals.GameSessions)
		}
		if s.Totals.PlaytimeMs != 15000 {
			t.Errorf("playtimeMs = %d, want 15000", s.Totals.PlaytimeMs)
		}
		if s.Totals.AvgSessionMs != 3000 {
			t.Errorf("avgSessionMs = %d, want 3000", s.Totals.AvgSessionMs)
		}
		if s.Totals.MedianSessionMs != 3000 {
			t.Errorf("medianSessionMs = %d, want 3000", s.Totals.MedianSessionMs)
		}
		if s.Totals.UniquePlayers != 2 {
			t.Errorf("uniquePlayers = %d, want 2", s.Totals.UniquePlayers)
		}
	})

	t.Run("byDay", func(t *testing.T) {
		s := sessionFixture(t)
		if len(s.ByDay) != 1 || s.ByDay[0].Sessions != 5 || s.ByDay[0].PlaytimeMs != 15000 {
			t.Fatalf("byDay = %+v", s.ByDay)
		}
	})

	t.Run("byGame", func(t *testing.T) {
		s := sessionFixture(t)
		byGame := map[string]GameBucket{}
		for _, g := range s.ByGame {
			byGame[g.GameID] = g
		}
		g1, g2 := byGame["g1"], byGame["g2"]
		if g1.Sessions != 3 || g1.PlaytimeMs != 9000 || g1.AvgSessionMs != 3000 || g1.MedianSessionMs != 3000 {
			t.Fatalf("g1 = %+v", g1)
		}
		if g1.UniquePlayers != 2 {
			t.Errorf("g1.uniquePlayers = %d, want 2", g1.UniquePlayers)
		}
		if g2.Sessions != 2 || g2.PlaytimeMs != 6000 || g2.AvgSessionMs != 3000 || g2.MedianSessionMs != 3000 {
			t.Fatalf("g2 = %+v", g2)
		}
		if g2.UniquePlayers != 1 {
			t.Errorf("g2.uniquePlayers = %d, want 1", g2.UniquePlayers)
		}
	})
}

// A session with no reported duration still counts toward GameSessions but
// must not pull the average/median down to a fake zero.
func TestSessionWithoutDurationDoesNotSkewAverage(t *testing.T) {
	h := New(t.TempDir())
	submit(t, h, `{"installId":"aaa","event":"game_session","gameId":"g1","durationMs":4000}`)
	submit(t, h, `{"installId":"bbb","event":"game_session","gameId":"g1"}`)

	s := summary(t, h, "")
	if s.Totals.GameSessions != 2 {
		t.Errorf("gameSessions = %d, want 2", s.Totals.GameSessions)
	}
	if s.Totals.AvgSessionMs != 4000 || s.Totals.MedianSessionMs != 4000 {
		t.Errorf("avg/median = %d/%d, want 4000/4000", s.Totals.AvgSessionMs, s.Totals.MedianSessionMs)
	}
}

func TestClearDropsBothGenerations(t *testing.T) {
	h := New(t.TempDir())
	submit(t, h, `{"event":"launcher_start"}`)
	w := httptest.NewRecorder()
	h.Clear(w, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://x/admin/api/metrics/clear", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("clear code = %d", w.Code)
	}
	if got := summary(t, h, "").Totals.Events; got != 0 {
		t.Fatalf("events after clear = %d, want 0", got)
	}
}

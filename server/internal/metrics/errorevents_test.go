package metrics

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

// errorEvents drives GET /admin/api/metrics/errors and decodes the answer.
func errorEvents(t *testing.T, h *Handlers, query string) (int, struct {
	Code   string  `json:"code"`
	Limit  int     `json:"limit"`
	Items  []Event `json:"items"`
	Capped bool    `json:"capped"`
}) {
	t.Helper()
	var out struct {
		Code   string  `json:"code"`
		Limit  int     `json:"limit"`
		Items  []Event `json:"items"`
		Capped bool    `json:"capped"`
	}
	req := httptest.NewRequestWithContext(t.Context(), http.MethodGet,
		"http://x/admin/api/metrics/errors"+query, nil)
	w := httptest.NewRecorder()
	h.ErrorEvents(w, req)
	if w.Code == http.StatusOK {
		if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
			t.Fatalf("bad json %q: %v", w.Body.String(), err)
		}
	}
	return w.Code, out
}

// The summary says "sync_failed — 8" and stops there. This endpoint exists so
// the next question — which build, which game — has an answer at all.
func TestErrorEventsReturnsMatchingEventsOnly(t *testing.T) {
	h := New(t.TempDir())
	for _, b := range []string{
		`{"installId":"aaa","event":"error","gameId":"g1","errorCode":"sync_failed","appVersion":"1.3.8"}`,
		`{"installId":"bbb","event":"error","gameId":"g2","errorCode":"sync_failed","appVersion":"1.3.7"}`,
		`{"installId":"ccc","event":"error","gameId":"g1","errorCode":"manifest_invalid"}`,
		`{"installId":"ddd","event":"launcher_start"}`,
	} {
		if w := submit(t, h, b); w.Code != http.StatusOK {
			t.Fatalf("submit %s -> %d", b, w.Code)
		}
	}

	code, out := errorEvents(t, h, "?code=sync_failed")
	if code != http.StatusOK {
		t.Fatalf("code = %d", code)
	}
	if len(out.Items) != 2 {
		t.Fatalf("got %d events, want 2: %+v", len(out.Items), out.Items)
	}
	for _, ev := range out.Items {
		if ev.ErrorCode != "sync_failed" {
			t.Errorf("leaked event with code %q", ev.ErrorCode)
		}
	}
	// Newest first: the recent ones are the ones being investigated.
	if out.Items[0].InstallID != "bbb" {
		t.Errorf("first item = %q, want the newest (bbb)", out.Items[0].InstallID)
	}
}

func TestErrorEventsFiltersByGame(t *testing.T) {
	h := New(t.TempDir())
	// gameIDOK only accepts games the registry knows; an unknown id is a 400,
	// so the filter is exercised through the summary's own allowlist instead.
	for _, b := range []string{
		`{"event":"error","gameId":"g1","errorCode":"sync_io"}`,
		`{"event":"error","gameId":"g2","errorCode":"sync_io"}`,
	} {
		if w := submit(t, h, b); w.Code != http.StatusOK {
			t.Fatalf("submit %s -> %d", b, w.Code)
		}
	}
	code, out := errorEvents(t, h, "?code=sync_io")
	if code != http.StatusOK || len(out.Items) != 2 {
		t.Fatalf("code=%d items=%d, want 200 and 2", code, len(out.Items))
	}
}

// An event stored without errorCode is counted under "unknown" in the summary,
// so the drill-down has to answer to the same name.
func TestErrorEventsMatchesUnknownCode(t *testing.T) {
	h := New(t.TempDir())
	if w := submit(t, h, `{"event":"error"}`); w.Code != http.StatusOK {
		t.Fatalf("submit -> %d", w.Code)
	}
	code, out := errorEvents(t, h, "?code=unknown")
	if code != http.StatusOK || len(out.Items) != 1 {
		t.Fatalf("code=%d items=%d, want 200 and 1", code, len(out.Items))
	}
}

func TestErrorEventsRequiresCode(t *testing.T) {
	h := New(t.TempDir())
	if code, _ := errorEvents(t, h, ""); code != http.StatusBadRequest {
		t.Fatalf("code = %d, want 400", code)
	}
}

func TestErrorEventsRejectsUnknownGame(t *testing.T) {
	h := New(t.TempDir())
	if code, _ := errorEvents(t, h, "?code=boom&gameId=../etc"); code != http.StatusBadRequest {
		t.Fatalf("code = %d, want 400", code)
	}
}

// The cap keeps a browser from pulling the whole store; when it bites, the UI
// must be told, otherwise "100 events" reads as the total.
func TestErrorEventsCapsAndFlags(t *testing.T) {
	h := New(t.TempDir())
	for i := range maxErrorEvents + 10 {
		if w := submit(t, h, `{"event":"error","errorCode":"sync_failed"}`); w.Code != http.StatusOK {
			t.Fatalf("submit #%d -> %d", i, w.Code)
		}
	}
	code, out := errorEvents(t, h, "?code=sync_failed")
	if code != http.StatusOK {
		t.Fatalf("code = %d", code)
	}
	if len(out.Items) != maxErrorEvents {
		t.Errorf("items = %d, want the cap %d", len(out.Items), maxErrorEvents)
	}
	if !out.Capped {
		t.Error("capped = false, want true so the UI can say so")
	}
}

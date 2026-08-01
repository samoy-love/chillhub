package feedback

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func submit(t *testing.T, h *Handlers, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequest(http.MethodPost, "http://example.com/feedback/submit", strings.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	w := httptest.NewRecorder()
	h.Submit(w, req)
	return w
}

// A body larger than the budget must be refused, not buffered and decoded.
// /feedback/submit is public and unauthenticated.
func TestSubmitRejectsOversizedBody(t *testing.T) {
	h := New(t.TempDir())
	huge := strings.Repeat("A", MaxBodyBytes+(64<<10))
	w := submit(t, h, `{"comment":"`+huge+`"}`)
	if w.Code == http.StatusOK {
		t.Fatal("an oversized submission was accepted")
	}
	items, _ := h.readAll()
	if len(items) != 0 {
		t.Fatalf("oversized submission was stored: %d items", len(items))
	}
}

// A malformed body must be reported, not stored as a blank report. The decode
// error used to be discarded with `_ =`, so garbage became an empty entry in
// the admin inbox.
func TestSubmitRejectsMalformedBody(t *testing.T) {
	h := New(t.TempDir())
	for _, body := range []string{"not json at all", `{"comment": `, `{{`} {
		w := submit(t, h, body)
		if w.Code != http.StatusBadRequest {
			t.Errorf("body %q: got %d, want 400", body, w.Code)
		}
	}
	items, _ := h.readAll()
	if len(items) != 0 {
		t.Fatalf("malformed submissions were stored as blank reports: %d items", len(items))
	}
}

// The free-form system map must be clamped: number of entries, key and value
// length. The inbox file is rewritten on every submit, so an unbounded map is a
// quadratic cost as well as a storage one.
func TestSubmitClampsSystemMap(t *testing.T) {
	h := New(t.TempDir())
	sys := map[string]string{}
	for i := 0; i < maxSystemEntries*3; i++ {
		sys[fmt.Sprintf("key-%03d-%s", i, strings.Repeat("k", 200))] = strings.Repeat("v", 4000)
	}
	payload, err := json.Marshal(map[string]any{"comment": "hi", "system": sys})
	if err != nil {
		t.Fatal(err)
	}
	if w := submit(t, h, string(payload)); w.Code != http.StatusOK {
		t.Fatalf("submit: %d %s", w.Code, w.Body.String())
	}

	items, err := h.readAll()
	if err != nil || len(items) != 1 {
		t.Fatalf("readAll: %v (%d items)", err, len(items))
	}
	got := items[0].System
	if len(got) > maxSystemEntries {
		t.Errorf("system map has %d entries, cap is %d", len(got), maxSystemEntries)
	}
	for k, v := range got {
		if len(k) > maxSystemKeyLen {
			t.Errorf("key of length %d exceeds %d", len(k), maxSystemKeyLen)
		}
		if len(v) > maxSystemValueLen {
			t.Errorf("value of length %d exceeds %d", len(v), maxSystemValueLen)
		}
	}
}

// A normal report still round-trips.
func TestSubmitAcceptsNormalReport(t *testing.T) {
	h := New(t.TempDir())
	w := submit(t, h, `{"name":"user","type":"bug","comment":"it broke","system":{"os":"Windows 11"}}`)
	if w.Code != http.StatusOK {
		t.Fatalf("submit: %d %s", w.Code, w.Body.String())
	}
	items, _ := h.readAll()
	if len(items) != 1 || items[0].Comment != "it broke" || items[0].System["os"] != "Windows 11" {
		t.Fatalf("report not stored correctly: %+v", items)
	}
}

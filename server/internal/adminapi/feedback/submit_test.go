package feedback

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
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

// A submit must cost one appended line, not a rewrite of the whole inbox: the
// endpoint is public, and rewriting a 64 MiB array per submission is quadratic.
func TestSubmitAppendsToJournalAndStaysReadable(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	for i := 0; i < 5; i++ {
		if w := submit(t, h, fmt.Sprintf(`{"comment":"report %d","type":"bug"}`, i)); w.Code != http.StatusOK {
			t.Fatalf("submit %d: %d %s", i, w.Code, w.Body.String())
		}
	}
	if _, err := os.Stat(filepath.Join(root, "feedback", "inbox.pending.ndjson")); err != nil {
		t.Fatalf("nothing was journalled: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "feedback", "inbox.json")); !os.IsNotExist(err) {
		t.Fatalf("the array file was rewritten for a handful of submissions: %v", err)
	}
	// The merged view is what every admin endpoint reads, newest first.
	items, err := h.readAll()
	if err != nil {
		t.Fatal(err)
	}
	if len(items) != 5 {
		t.Fatalf("readAll returned %d items, want 5", len(items))
	}
	if items[0].Comment != "report 4" {
		t.Fatalf("items are not newest-first: %q", items[0].Comment)
	}
	// An admin write compacts the journal away.
	w := httptest.NewRecorder()
	h.Delete(w, httptest.NewRequest(http.MethodPost, "http://example.com/x?id="+items[0].ID, nil))
	if w.Code != http.StatusOK {
		t.Fatalf("delete: %d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "feedback", "inbox.pending.ndjson")); !os.IsNotExist(err) {
		t.Fatalf("journal survived a compaction: %v", err)
	}
	items, _ = h.readAll()
	if len(items) != 4 {
		t.Fatalf("after delete: %d items, want 4", len(items))
	}
}

// itemSize must account for JSON escaping: a log bundle of newlines and quotes
// costs several bytes per character in the stored file.
func TestItemSizeCountsEscaping(t *testing.T) {
	raw := strings.Repeat("\n\"", 1000)
	plain := strings.Repeat("ab", 1000)
	if itemSize(Item{Logs: raw}) <= itemSize(Item{Logs: plain}) {
		t.Fatal("escaped characters must not be counted as one byte each")
	}
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

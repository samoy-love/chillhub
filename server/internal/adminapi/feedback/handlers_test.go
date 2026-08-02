package feedback

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// submit posts a public report and returns the response.
func postReport(t *testing.T, h *Handlers, body string) *httptest.ResponseRecorder {
	t.Helper()
	r := httptest.NewRequest(http.MethodPost, "/feedback/submit", strings.NewReader(body))
	r.Header.Set("Content-Type", "application/json")
	w := httptest.NewRecorder()
	h.Submit(w, r)
	return w
}

// listItems calls List and decodes the inbox.
func listItems(t *testing.T, h *Handlers, query string) []Item {
	t.Helper()
	r := httptest.NewRequest(http.MethodGet, "/admin/api/feedback/list?"+query, nil)
	w := httptest.NewRecorder()
	h.List(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("List returned %d: %s", w.Code, w.Body.String())
	}
	var out struct {
		Items []Item `json:"items"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatalf("List body is not JSON: %v", err)
	}
	return out.Items
}

// A submitted report must be visible to the admin. This is the whole point of the
// feature, and it crosses the NDJSON journal → inbox rebuild that was introduced
// to stop the public endpoint from rewriting the entire file on every call.
func TestSubmittedReportBecomesVisible(t *testing.T) {
	h := New(t.TempDir())

	w := postReport(t, h, `{"name":"Иван","contact":"@ivan","type":"bug","comment":"игра не ставится"}`)
	if w.Code != http.StatusOK {
		t.Fatalf("submit returned %d: %s", w.Code, w.Body.String())
	}

	items := listItems(t, h, "")
	if len(items) != 1 {
		t.Fatalf("inbox holds %d items, want 1", len(items))
	}
	if items[0].Comment != "игра не ставится" {
		t.Errorf("comment lost: %q", items[0].Comment)
	}
	if items[0].ID == "" {
		t.Error("the report has no id — nothing can reference it afterwards")
	}
	if items[0].Status != "new" {
		t.Errorf("a fresh report has status %q, want new", items[0].Status)
	}
}

// Several submissions must all survive: the journal is appended to, and the
// rebuild must not drop or merge entries.
func TestEverySubmissionSurvives(t *testing.T) {
	h := New(t.TempDir())
	const n = 12
	for i := 0; i < n; i++ {
		if w := postReport(t, h, `{"name":"u","type":"idea","comment":"c"}`); w.Code != http.StatusOK {
			t.Fatalf("submit %d returned %d", i, w.Code)
		}
	}
	if got := len(listItems(t, h, "")); got != n {
		t.Fatalf("inbox holds %d items, want %d", got, n)
	}
}

// Marking read must persist — the unread badge is driven by it.
func TestMarkReadPersists(t *testing.T) {
	h := New(t.TempDir())
	postReport(t, h, `{"name":"u","type":"bug","comment":"c"}`)
	id := listItems(t, h, "")[0].ID

	r := httptest.NewRequest(http.MethodPost, "/admin/api/feedback/markRead?id="+id, nil)
	w := httptest.NewRecorder()
	h.MarkRead(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("markRead returned %d: %s", w.Code, w.Body.String())
	}

	if got := listItems(t, h, "")[0].Status; got != "read" {
		t.Fatalf("status = %q after markRead, want read", got)
	}
}

// The important flag toggles both ways; the admin uses it to keep a report in sight.
func TestToggleImportantFlipsBothWays(t *testing.T) {
	h := New(t.TempDir())
	postReport(t, h, `{"name":"u","type":"bug","comment":"c"}`)
	id := listItems(t, h, "")[0].ID

	toggle := func() bool {
		r := httptest.NewRequest(http.MethodPost, "/admin/api/feedback/toggleImportant?id="+id, nil)
		w := httptest.NewRecorder()
		h.ToggleImportant(w, r)
		if w.Code != http.StatusOK {
			t.Fatalf("toggleImportant returned %d", w.Code)
		}
		return listItems(t, h, "")[0].Important
	}

	if !toggle() {
		t.Fatal("first toggle did not set the flag")
	}
	if toggle() {
		t.Fatal("second toggle did not clear the flag")
	}
}

// A deleted report must disappear from the inbox.
func TestDeleteRemovesFromInbox(t *testing.T) {
	h := New(t.TempDir())
	postReport(t, h, `{"name":"u","type":"bug","comment":"первое"}`)
	postReport(t, h, `{"name":"u","type":"bug","comment":"второе"}`)
	items := listItems(t, h, "")
	if len(items) != 2 {
		t.Fatalf("setup produced %d items", len(items))
	}

	r := httptest.NewRequest(http.MethodPost, "/admin/api/feedback/delete?id="+items[0].ID, nil)
	w := httptest.NewRecorder()
	h.Delete(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("delete returned %d: %s", w.Code, w.Body.String())
	}

	left := listItems(t, h, "")
	if len(left) != 1 {
		t.Fatalf("inbox holds %d items after delete, want 1", len(left))
	}
	if left[0].ID == items[0].ID {
		t.Error("the wrong report was deleted")
	}
}

// Get returns one report by id and refuses an unknown one instead of returning
// an empty object the UI would render as a blank card.
func TestGetByIDAndUnknownID(t *testing.T) {
	h := New(t.TempDir())
	postReport(t, h, `{"name":"Иван","type":"bug","comment":"текст"}`)
	id := listItems(t, h, "")[0].ID

	r := httptest.NewRequest(http.MethodGet, "/admin/api/feedback/get?id="+id, nil)
	w := httptest.NewRecorder()
	h.Get(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("get returned %d", w.Code)
	}
	var item Item
	if err := json.Unmarshal(w.Body.Bytes(), &item); err != nil {
		t.Fatalf("get body is not JSON: %v", err)
	}
	if item.Comment != "текст" {
		t.Errorf("wrong report returned: %+v", item)
	}

	r = httptest.NewRequest(http.MethodGet, "/admin/api/feedback/get?id=нет-такого", nil)
	w = httptest.NewRecorder()
	h.Get(w, r)
	if w.Code == http.StatusOK {
		t.Error("an unknown id returned 200")
	}
}

// Clear empties the inbox — it is the "start over" button.
func TestClearEmptiesInbox(t *testing.T) {
	h := New(t.TempDir())
	for i := 0; i < 3; i++ {
		postReport(t, h, `{"name":"u","type":"bug","comment":"c"}`)
	}

	r := httptest.NewRequest(http.MethodPost, "/admin/api/feedback/clear", nil)
	w := httptest.NewRecorder()
	h.Clear(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("clear returned %d", w.Code)
	}
	if got := len(listItems(t, h, "")); got != 0 {
		t.Fatalf("inbox still holds %d items", got)
	}
}

// The public endpoint is unauthenticated, so a body that is not JSON must be
// refused cleanly rather than panicking the admin process.
func TestSubmitRejectsGarbage(t *testing.T) {
	h := New(t.TempDir())
	for _, body := range []string{"не json", "", "[]", `{"comment":`} {
		w := postReport(t, h, body)
		if w.Code == http.StatusOK && body != "" {
			t.Errorf("garbage body %q was accepted", body)
		}
	}
	// Whatever happened, the inbox must still be readable.
	listItems(t, h, "")
}

// An oversized diagnostics bundle is clamped, not stored whole: the inbox file is
// rewritten on compaction, so one report must not be able to inflate it without bound.
func TestOversizedBundleIsClamped(t *testing.T) {
	h := New(t.TempDir())
	huge := strings.Repeat("x", MaxLogBytes+50_000)
	body, _ := json.Marshal(map[string]any{
		"name": "u", "type": "bug", "comment": "c", "attachLogs": true, "logs": huge,
	})
	if w := postReport(t, h, string(body)); w.Code != http.StatusOK {
		t.Fatalf("submit returned %d: %s", w.Code, w.Body.String())
	}

	items := listItems(t, h, "")
	if len(items) != 1 {
		t.Fatalf("inbox holds %d items", len(items))
	}
	if len(items[0].Logs) > MaxLogBytes {
		t.Fatalf("stored bundle is %d bytes, above the %d limit", len(items[0].Logs), MaxLogBytes)
	}
}

// The free-form system map comes from the client and must be bounded in every
// dimension: entry count, key length and value length.
func TestSystemMapIsClamped(t *testing.T) {
	h := New(t.TempDir())
	sys := map[string]string{}
	for i := 0; i < maxSystemEntries*3; i++ {
		sys[strings.Repeat("k", maxSystemKeyLen*2)+string(rune('a'+i%26))] = strings.Repeat("v", maxSystemValueLen*2)
	}
	body, _ := json.Marshal(map[string]any{"name": "u", "type": "bug", "comment": "c", "system": sys})
	if w := postReport(t, h, string(body)); w.Code != http.StatusOK {
		t.Fatalf("submit returned %d", w.Code)
	}

	got := listItems(t, h, "")[0].System
	if len(got) > maxSystemEntries {
		t.Errorf("system map kept %d entries, limit is %d", len(got), maxSystemEntries)
	}
	for k, v := range got {
		if len(k) > maxSystemKeyLen {
			t.Errorf("key of %d bytes exceeds %d", len(k), maxSystemKeyLen)
		}
		if len(v) > maxSystemValueLen {
			t.Errorf("value of %d bytes exceeds %d", len(v), maxSystemValueLen)
		}
	}
}

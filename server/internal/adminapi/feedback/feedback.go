// Package feedback stores user reports submitted from the launcher and serves
// the admin endpoints that browse them.
//
// The inbox is a single JSON file that is fully read and rewritten on every
// submit, so both the per-item and the total size are bounded (see Prune).
package feedback

import (
	"encoding/json"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"

	"ChillHub/server/internal/adminutil"
)

// Item is a single user report as stored in the inbox file.
type Item struct {
	ID         string            `json:"id"`
	CreatedAt  string            `json:"createdAt"`
	Type       string            `json:"type"` // bug | idea | question | other
	Name       string            `json:"name"`
	Contact    string            `json:"contact"`
	Comment    string            `json:"comment"`
	Important  bool              `json:"important"`
	Status     string            `json:"status"` // new | read | deleted
	AttachLogs bool              `json:"attachLogs"`
	Logs       string            `json:"logs,omitempty"`
	System     map[string]string `json:"system,omitempty"`
}

// Storage limits. The inbox is a single JSON file that is fully read and
// rewritten on every submit, so both the per-item and the total size must stay
// bounded, otherwise the public submit endpoint degrades to O(n^2).
const (
	// MaxLogBytes is the max size of the diagnostics bundle accepted with a single report.
	MaxLogBytes = 256 << 10 // 256 KiB
	// MaxItems is the max number of reports kept in the inbox.
	MaxItems = 2000
	// MaxTotalBytes is a soft budget for the whole inbox file.
	MaxTotalBytes = 64 << 20 // 64 MiB
)

// Handlers serves the feedback endpoints for one content root.
type Handlers struct {
	root string
	mu   sync.Mutex
	// CurrentUser resolves the acting admin for audit log lines. It may be nil.
	CurrentUser func(*http.Request) string
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

func (h *Handlers) readAll() ([]Item, error) {
	b, err := os.ReadFile(h.path())
	if err != nil {
		if os.IsNotExist(err) {
			return []Item{}, nil
		}
		return nil, err
	}
	var items []Item
	if err := json.Unmarshal(b, &items); err != nil {
		return []Item{}, nil
	}
	return items, nil
}

func (h *Handlers) writeAll(items []Item) error {
	if err := os.MkdirAll(h.dir(), 0o755); err != nil {
		return err
	}
	b2, _ := json.MarshalIndent(items, "", "  ")
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
	return nil
}

// itemSize is a cheap estimate of the JSON footprint of one item.
func itemSize(it Item) int {
	n := len(it.ID) + len(it.CreatedAt) + len(it.Type) + len(it.Name) +
		len(it.Contact) + len(it.Comment) + len(it.Logs) + 128
	for k, v := range it.System {
		n += len(k) + len(v) + 8
	}
	return n
}

// Prune enforces the count and total-size limits.
// items must be ordered newest-first (as stored). Oldest non-important reports
// are discarded first; if the size budget is still exceeded, log bundles of the
// oldest reports are dropped, and only then whole reports.
func Prune(items []Item) []Item {
	if len(items) > MaxItems {
		removed := make([]bool, len(items))
		drop := len(items) - MaxItems
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
		items = kept
	}

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
	// Accept JSON body
	var in struct {
		Name       string            `json:"name"`
		Contact    string            `json:"contact"`
		Comment    string            `json:"comment"`
		Type       string            `json:"type"`
		AttachLogs bool              `json:"attachLogs"`
		Logs       string            `json:"logs"`
		System     map[string]string `json:"system"`
	}
	dec := json.NewDecoder(r.Body)
	_ = dec.Decode(&in)
	// sanitize inputs and limit lengths to prevent abuse; PRESERVE whitespace/newlines for Comment
	clamp := func(s string, n int) string {
		if len(s) <= n {
			return s
		}
		return s[:n]
	}
	sName := strings.TrimSpace(clamp(in.Name, 200))
	sContact := strings.TrimSpace(clamp(in.Contact, 200))
	rawComment := clamp(in.Comment, 5000) // keep as-is to preserve newlines and spaces
	// Diagnostics bundles are capped: the whole inbox is rewritten on each submit
	sLogs := clamp(in.Logs, MaxLogBytes)
	t := strings.ToLower(strings.TrimSpace(in.Type))
	switch t {
	case "bug", "idea", "question":
	default:
		t = "other"
	}
	item := Item{
		ID:         adminutil.GenID(),
		CreatedAt:  time.Now().UTC().Format(time.RFC3339),
		Type:       t,
		Name:       sName,
		Contact:    sContact,
		Comment:    rawComment,
		Important:  false,
		Status:     "new",
		AttachLogs: in.AttachLogs,
		Logs:       sLogs,
		System:     in.System,
	}
	h.mu.Lock()
	items, _ := h.readAll()
	items = append([]Item{item}, items...) // prepend newest
	items = Prune(items)                   // rotate out the oldest reports
	err := h.writeAll(items)
	h.mu.Unlock()
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
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
	// Filters
	fType := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("type")))
	fImp := strings.TrimSpace(r.URL.Query().Get("important"))
	fQ := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("q")))
	fStatus := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("status")))
	fFrom := strings.TrimSpace(r.URL.Query().Get("from"))
	fTo := strings.TrimSpace(r.URL.Query().Get("to"))
	fAuto := strings.TrimSpace(r.URL.Query().Get("auto"))
	var fromT, toT time.Time
	if fFrom != "" {
		if t, err := time.Parse(time.RFC3339, fFrom); err == nil {
			fromT = t
		}
	}
	if fTo != "" {
		if t, err := time.Parse(time.RFC3339, fTo); err == nil {
			toT = t
		}
	}
	out := make([]Item, 0, len(items))
	for _, it := range items {
		if fType != "" && strings.ToLower(it.Type) != fType {
			continue
		}
		if fStatus != "" && strings.ToLower(it.Status) != fStatus {
			continue
		}
		if fImp != "" {
			want := fImp == "1" || strings.EqualFold(fImp, "true")
			if it.Important != want {
				continue
			}
		}
		if fAuto != "" {
			want := fAuto == "1" || strings.EqualFold(fAuto, "true")
			got := false
			if it.System != nil {
				if v, ok := it.System["auto"]; ok {
					got = (v == "1" || strings.EqualFold(v, "true"))
				}
			}
			if want != got {
				continue
			}
		}
		if !fromT.IsZero() || !toT.IsZero() {
			if t, err := time.Parse(time.RFC3339, it.CreatedAt); err == nil {
				if !fromT.IsZero() && t.Before(fromT) {
					continue
				}
				if !toT.IsZero() && t.After(toT) {
					continue
				}
			}
		}
		if fQ != "" {
			hay := strings.ToLower(it.Name + "\n" + it.Contact + "\n" + it.Comment)
			if !strings.Contains(hay, fQ) {
				continue
			}
		}
		if it.Status == "deleted" {
			continue
		}
		out = append(out, it)
	}
	// Sort by CreatedAt desc
	sort.Slice(out, func(i, j int) bool { return out[i].CreatedAt > out[j].CreatedAt })
	adminutil.WriteJSON(w, struct {
		Items []Item `json:"items"`
	}{Items: out})
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
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	log.Printf("[audit] feedback delete id=%s by=%s", id, h.user(r))
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
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	log.Printf("[audit] feedback important-toggle id=%s now=%v by=%s", id, newVal, h.user(r))
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
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	log.Printf("[audit] feedback %s id=%s by=%s", audit, id, h.user(r))
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
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	log.Printf("[audit] feedback clear by=%s", h.user(r))
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

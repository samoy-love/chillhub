package main

import (
	"archive/zip"
	"bytes"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"image"
	"image/jpeg"
	"image/png"

	"ChillHub/server/internal/httpx"

	"github.com/zeebo/blake3"
	"go.uber.org/automaxprocs/maxprocs"
	"golang.org/x/text/encoding/charmap"
)

var contentRoot = detectContentRoot()

func init() {
	// Configure GOMAXPROCS automatically. On Windows (no cgroup quotas) suppress noisy info message.
	_, err := maxprocs.Set(maxprocs.Logger(func(format string, a ...any) {
		if runtime.GOOS == "windows" {
			return
		}
		log.Printf("[maxprocs] "+format, a...)
	}))
	if err != nil {
		log.Printf("[maxprocs] set failed: %v", err)
	}
}

func detectContentRoot() string {
	if v := os.Getenv("CONTENT_ROOT"); v != "" {
		return v
	}
	if exe, err := os.Executable(); err == nil && exe != "" {
		d := filepath.Dir(exe)
		for i := 0; i < 6; i++ {
			p := filepath.Join(d, "content")
			if st, err := os.Stat(p); err == nil && st.IsDir() {
				return p
			}
			d = filepath.Dir(d)
		}
	}
	if st, err := os.Stat("content"); err == nil && st.IsDir() {
		return "content"
	}
	return "."
}

// simple per-IP rate limiter (window-based) for feedback
var (
	fbMu sync.Mutex
	fbRL = make(map[string]struct {
		Count       int
		WindowStart time.Time
	})
)

func clientIP(r *http.Request) string {
	// prioritize X-Forwarded-For then X-Real-IP
	if xff := strings.TrimSpace(r.Header.Get("X-Forwarded-For")); xff != "" {
		if i := strings.IndexByte(xff, ','); i >= 0 {
			return strings.TrimSpace(xff[:i])
		}
		return xff
	}
	if rip := strings.TrimSpace(r.Header.Get("X-Real-IP")); rip != "" {
		return rip
	}
	host := strings.TrimSpace(r.RemoteAddr)
	if i := strings.LastIndexByte(host, ':'); i > 0 {
		host = host[:i]
	}
	return host
}

// optional wrapper if we want to separate limiter from handler registration
func rateLimitFeedbackSubmit(h http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodOptions {
			h(w, r)
			return
		}
		if r.Method != http.MethodPost {
			h(w, r)
			return
		}
		ip := clientIP(r)
		now := time.Now()
		const limit = 5
		const window = time.Minute
		fbMu.Lock()
		st := fbRL[ip]
		if st.WindowStart.IsZero() || now.Sub(st.WindowStart) > window {
			st = struct {
				Count       int
				WindowStart time.Time
			}{Count: 0, WindowStart: now}
		}
		if st.Count >= limit {
			fbMu.Unlock()
			http.Error(w, "too many requests", http.StatusTooManyRequests)
			return
		}
		st.Count++
		fbRL[ip] = st
		fbMu.Unlock()
		h(w, r)
	}
}

// ===== Feedback storage and handlers =====
type FeedbackItem struct {
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

func feedbackDir() string  { return filepath.Join(contentRoot, "feedback") }
func feedbackPath() string { return filepath.Join(feedbackDir(), "inbox.json") }

func readFeedbackAll() ([]FeedbackItem, error) {
	p := feedbackPath()
	b, err := os.ReadFile(p)
	if err != nil {
		if os.IsNotExist(err) {
			return []FeedbackItem{}, nil
		}
		return nil, err
	}
	var items []FeedbackItem
	if err := json.Unmarshal(b, &items); err != nil {
		return []FeedbackItem{}, nil
	}
	return items, nil
}

func writeFeedbackAll(items []FeedbackItem) error {
	if err := os.MkdirAll(feedbackDir(), 0o755); err != nil {
		return err
	}
	b2, _ := json.MarshalIndent(items, "", "  ")
	return os.WriteFile(feedbackPath(), b2, 0o644)
}

func genID() string {
	var b [12]byte
	if _, err := rand.Read(b[:]); err != nil {
		return fmt.Sprintf("id-%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(b[:])
}

// Public submit: accepts JSON or form; fields: name, contact, comment, type, attachLogs, logs, system
func handleFeedbackSubmit(w http.ResponseWriter, r *http.Request) {
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
	max := func(s string, n int) string {
		if len(s) <= n {
			return s
		}
		return s[:n]
	}
	sName := strings.TrimSpace(max(in.Name, 200))
	sContact := strings.TrimSpace(max(in.Contact, 200))
	rawComment := max(in.Comment, 5000) // keep as-is to preserve newlines and spaces
	// Allow large diagnostics bundles (up to ~2 MB) and preserve whitespace
	sLogs := max(in.Logs, 2*1024*1024)
	t := strings.ToLower(strings.TrimSpace(in.Type))
	switch t {
	case "bug", "idea", "question":
	default:
		t = "other"
	}
	item := FeedbackItem{
		ID:         genID(),
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
	items, _ := readFeedbackAll()
	items = append([]FeedbackItem{item}, items...) // prepend newest
	if err := writeFeedbackAll(items); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]any{"status": "ok", "id": item.ID})
}

// Admin: list with filters: type, important(1/0), q (search in comment/contact/name), status, from, to
func handleFeedbackList(w http.ResponseWriter, r *http.Request) {
	items, _ := readFeedbackAll()
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
	out := make([]FeedbackItem, 0, len(items))
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
	writeJSON(w, struct {
		Items []FeedbackItem `json:"items"`
	}{Items: out})
}

func handleFeedbackGet(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	items, _ := readFeedbackAll()
	for _, it := range items {
		if it.ID == id {
			writeJSON(w, it)
			return
		}
	}
	http.Error(w, "not found", http.StatusNotFound)
}

func handleFeedbackDelete(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	items, _ := readFeedbackAll()
	out := make([]FeedbackItem, 0, len(items))
	for _, it := range items {
		if it.ID == id {
			continue
		} // hard delete
		out = append(out, it)
	}
	if err := writeFeedbackAll(out); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	_, user := currentUser(r)
	log.Printf("[audit] feedback delete id=%s by=%s", id, user)
	writeJSON(w, map[string]string{"status": "ok"})
}

func handleFeedbackToggleImportant(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	items, _ := readFeedbackAll()
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
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	if err := writeFeedbackAll(items); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	_, user := currentUser(r)
	log.Printf("[audit] feedback important-toggle id=%s now=%v by=%s", id, newVal, user)
	writeJSON(w, map[string]string{"status": "ok"})
}

func handleFeedbackMarkRead(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	items, _ := readFeedbackAll()
	changed := false
	for i := range items {
		if items[i].ID == id {
			items[i].Status = "read"
			changed = true
			break
		}
	}
	if !changed {
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	if err := writeFeedbackAll(items); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	_, user := currentUser(r)
	log.Printf("[audit] feedback mark-read id=%s by=%s", id, user)
	writeJSON(w, map[string]string{"status": "ok"})
}

// allow reverting item back to unread (status=new)
func handleFeedbackMarkUnread(w http.ResponseWriter, r *http.Request) {
	id := strings.TrimSpace(r.URL.Query().Get("id"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	items, _ := readFeedbackAll()
	changed := false
	for i := range items {
		if items[i].ID == id {
			items[i].Status = "new"
			changed = true
			break
		}
	}
	if !changed {
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	if err := writeFeedbackAll(items); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	_, user := currentUser(r)
	log.Printf("[audit] feedback mark-unread id=%s by=%s", id, user)
	writeJSON(w, map[string]string{"status": "ok"})
}

func handleFeedbackClear(w http.ResponseWriter, r *http.Request) {
	if err := writeFeedbackAll([]FeedbackItem{}); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	_, user := currentUser(r)
	log.Printf("[audit] feedback clear by=%s", user)
	writeJSON(w, map[string]string{"status": "ok"})
}

// estimateZipUncompressedSize sums UncompressedSize64 of all regular files in the ZIP.
func estimateZipUncompressedSize(zipPath string) (uint64, error) {
	r, err := zip.OpenReader(zipPath)
	if err != nil {
		return 0, err
	}
	defer r.Close()
	var total uint64
	for _, f := range r.File {
		if f.FileInfo().IsDir() {
			continue
		}
		total += f.UncompressedSize64
	}
	return total, nil
}

// getFreeSpaceBytes returns available free bytes on the filesystem that contains the given path.
func getFreeSpaceBytes(path string) (uint64, error) {
	// Ensure path exists to resolve volume/root correctly
	base := path
	if base == "" {
		base = "."
	}
	if _, err := os.Stat(base); os.IsNotExist(err) {
		if err2 := os.MkdirAll(base, 0o755); err2 != nil {
			// fallback to its parent
			base = filepath.Dir(base)
		}
	}
	return getFreeSpaceBytesImpl(base)
}

// handleUploadStream uploads a ZIP and streams progress (NDJSON): start, unzip entries, compose files, done
func handleUploadStream(w http.ResponseWriter, r *http.Request) {
	// Enforce auth here (since nginx bypasses auth_request for this endpoint)
	if _, user := currentUser(r); user == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	// Streaming setup (tolerate environments without http.Flusher)
	w.Header().Set("Content-Type", "application/x-ndjson")
	type flusher interface{ Flush() }
	var fl flusher
	if f, ok := w.(http.Flusher); ok {
		fl = f
	} else {
		fl = flusher(noopFlusher{})
	}

	// Prefer true streaming to avoid extra disk copies and huge memory use
	mr, err := r.MultipartReader()
	if err != nil {
		http.Error(w, "multipart reader error: "+err.Error(), http.StatusBadRequest)
		return
	}

	// Collect fields and stream the file part directly to temp ZIP on disk
	var (
		kind         string
		gid          string
		ver          string
		upd          bool
		origFilename string
		tmpName      string
		saved        int64
	)

	// Optional precheck: ensure enough temp space based on Content-Length if known
	if r.ContentLength > 0 {
		tmpDir := filepath.Join(contentRoot, "tmp")
		if err := os.MkdirAll(tmpDir, 0o755); err == nil {
			if free, ferr := getFreeSpaceBytes(tmpDir); ferr == nil && free > 0 && uint64(r.ContentLength) > free {
				http.Error(w, fmt.Sprintf("insufficient temp space: need %d bytes, have %d bytes", r.ContentLength, free), http.StatusInsufficientStorage)
				return
			}
		}
	}

	// Iterate parts
	for {
		part, perr := mr.NextPart()
		if perr == io.EOF {
			break
		}
		if perr != nil {
			http.Error(w, perr.Error(), http.StatusBadRequest)
			return
		}
		name := strings.TrimSpace(part.FormName())
		if name == "" {
			// skip unnamed parts
			io.Copy(io.Discard, part)
			_ = part.Close()
			continue
		}
		switch name {
		case "kind":
			b, _ := io.ReadAll(io.LimitReader(part, 1<<20))
			kind = strings.ToLower(strings.TrimSpace(string(b)))
		case "gameId":
			b, _ := io.ReadAll(io.LimitReader(part, 1<<20))
			gid = strings.TrimSpace(string(b))
		case "version":
			b, _ := io.ReadAll(io.LimitReader(part, 1<<20))
			ver = strings.TrimSpace(string(b))
		case "updateLatest":
			b, _ := io.ReadAll(io.LimitReader(part, 1<<20))
			upd = strings.TrimSpace(string(b)) == "1"
		case "zip":
			// Create temp dir and file once we hit the file part; stream directly
			tmpDir := filepath.Join(contentRoot, "tmp")
			if err := os.MkdirAll(tmpDir, 0o755); err != nil {
				fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
				fl.Flush()
				part.Close()
				return
			}
			tmpZip, err := os.CreateTemp(tmpDir, "upload-*.zip")
			if err != nil {
				fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
				fl.Flush()
				part.Close()
				return
			}
			tmpName = tmpZip.Name()
			origFilename = part.FileName()
			// Copy with a larger buffer for better throughput on big uploads
			buf := make([]byte, 4<<20) // 4 MiB
			n, cerr := io.CopyBuffer(tmpZip, part, buf)
			// Close resources
			_ = tmpZip.Close()
			_ = part.Close()
			if cerr != nil {
				os.Remove(tmpName)
				fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", cerr.Error())
				fl.Flush()
				return
			}
			saved = n
			fmt.Fprintf(w, "{\"type\":\"zipSaved\",\"filename\":%q,\"bytes\":%d}\n", origFilename, saved)
			fl.Flush()
		default:
			// Consume but ignore other fields
			io.Copy(io.Discard, part)
		}
		_ = part.Close()
	}

	if kind == "launcher" {
		gid = "launcher"
	}
	if kind == "" {
		http.Error(w, "missing kind (launcher|game)", http.StatusBadRequest)
		return
	}
	if ver == "" {
		http.Error(w, "missing version", http.StatusBadRequest)
		return
	}
	if kind == "game" && strings.TrimSpace(gid) == "" {
		http.Error(w, "missing gameId for kind=game", http.StatusBadRequest)
		return
	}
	if !isSafeGameID(gid) || !isSafeVersion(ver) {
		http.Error(w, "invalid gameId or version", http.StatusBadRequest)
		return
	}
	if tmpName == "" {
		fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", "missing zip part")
		fl.Flush()
		return
	}

	// Send start event after we know parameters
	fmt.Fprintf(w, "{\"type\":\"start\",\"kind\":%q,\"gameId\":%q,\"version\":%q}\n", kind, gid, ver)
	fl.Flush()

	// Save zip to temp was already done (tmpName). Ensure filesRoot for extraction exists on same FS
	filesRoot := filepath.Join(contentRoot, "content", gid, ver, "files")
	if err := os.MkdirAll(filesRoot, 0o755); err != nil {
		fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
		fl.Flush()
		return
	}

	// Prepare extraction dir (already ensured above)

	// Check free space before unzip (estimate total uncompressed size of ZIP)
	if needBytes, err := estimateZipUncompressedSize(tmpName); err == nil {
		if freeBytes, ferr := getFreeSpaceBytes(filesRoot); ferr == nil && freeBytes > 0 && needBytes > freeBytes {
			http.Error(w, fmt.Sprintf("insufficient disk space: need %d bytes, have %d bytes", needBytes, freeBytes), http.StatusInsufficientStorage)
			return
		}
	}

	// Unzip with progress
	zr, err := zip.OpenReader(tmpName)
	if err != nil {
		fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
		fl.Flush()
		return
	}
	for _, zf := range zr.File {
		name := zipFileDecodedName(zf)
		rel := filepath.ToSlash(strings.TrimLeft(strings.TrimSpace(name), "/\\"))
		rel = filepath.ToSlash(filepath.Clean(rel))
		if rel == "." || rel == "" {
			continue
		}
		full := filepath.Join(filesRoot, rel)
		if !ensureWithin(filesRoot, full) {
			zr.Close()
			fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", "zip entry outside target: "+rel)
			fl.Flush()
			return
		}
		if zf.FileInfo().IsDir() || strings.HasSuffix(rel, "/") {
			_ = os.MkdirAll(full, 0o755)
			fmt.Fprintf(w, "{\"type\":\"unzip\",\"path\":%q}\n", rel)
			fl.Flush()
			continue
		}
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			zr.Close()
			fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
			fl.Flush()
			return
		}
		rc, err := zf.Open()
		if err != nil {
			zr.Close()
			fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
			fl.Flush()
			return
		}
		out, err := os.OpenFile(full, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o644)
		if err != nil {
			rc.Close()
			zr.Close()
			fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
			fl.Flush()
			return
		}
		if _, err := io.Copy(out, rc); err != nil {
			out.Close()
			rc.Close()
			zr.Close()
			fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
			fl.Flush()
			return
		}
		out.Close()
		rc.Close()
		fmt.Fprintf(w, "{\"type\":\"unzip\",\"path\":%q}\n", rel)
		fl.Flush()
	}
	zr.Close()
	// Remove temp zip
	os.Remove(tmpName)

	// Compose manifest with progress (like handleComposeStream)
	// 1) pre-scan totals
	var totalFiles int
	var totalBytes int64
	filepath.WalkDir(filesRoot, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		info, _ := d.Info()
		totalFiles++
		totalBytes += info.Size()
		return nil
	})
	fmt.Fprintf(w, "{\"type\":\"composeStart\",\"totalFiles\":%d,\"totalBytes\":%d}\n", totalFiles, totalBytes)
	fl.Flush()

	var files []manifestFile
	dirHasFile := map[string]bool{}
	allDirs := map[string]bool{}
	var idx int
	var bytesDone int64
	errWalk := filepath.WalkDir(filesRoot, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(filesRoot, path)
		rel = filepath.ToSlash(rel)
		if d.IsDir() {
			allDirs[rel] = true
			return nil
		}
		if rel == "." {
			return nil
		}
		info, _ := d.Info()
		f, err := os.Open(path)
		if err != nil {
			return err
		}
		hSha := sha256.New()
		hB3 := blake3.New()
		if _, err := io.Copy(io.MultiWriter(hSha, hB3), f); err != nil {
			f.Close()
			return err
		}
		f.Close()
		files = append(files, manifestFile{
			Path:       rel,
			Size:       info.Size(),
			Blake3:     hex.EncodeToString(hB3.Sum(nil)),
			Sha256:     hex.EncodeToString(hSha.Sum(nil)),
			Executable: isExecutable(rel),
		})
		p := filepath.ToSlash(filepath.Dir(rel))
		for p != "." && p != "/" {
			dirHasFile[p] = true
			p = filepath.ToSlash(filepath.Dir(p))
		}
		idx++
		bytesDone += info.Size()
		fmt.Fprintf(w, "{\"type\":\"file\",\"idx\":%d,\"path\":%q,\"bytesDone\":%d}\n", idx, rel, bytesDone)
		fl.Flush()
		return nil
	})
	if errWalk != nil {
		fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", errWalk.Error())
		fl.Flush()
		return
	}

	emptyDirs := make([]string, 0)
	for d := range allDirs {
		if d == "." || d == "" {
			continue
		}
		if !dirHasFile[d] {
			emptyDirs = append(emptyDirs, ensureTrailingSlash(d))
		}
	}
	sort.Strings(emptyDirs)
	m := manifest{
		Version:   ver,
		BuildID:   newBuildID(),
		GameID:    gid,
		CreatedAt: time.Now().UTC().Format(time.RFC3339),
		Files:     files,
		EmptyDirs: emptyDirs,
		Signature: "dev-mock-signature",
	}

	outDir := filepath.Join(contentRoot, "manifests", gid)
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
		fl.Flush()
		return
	}
	outPath := filepath.Join(outDir, ver+".json")
	b, _ := json.MarshalIndent(m, "", "  ")
	if err := os.WriteFile(outPath, b, 0o644); err != nil {
		fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
		fl.Flush()
		return
	}
	if upd {
		bl, _ := json.MarshalIndent(map[string]string{"version": ver}, "", "  ")
		_ = os.WriteFile(filepath.Join(outDir, "latest.json"), bl, 0o644)
	}

	fmt.Fprintf(w, "{\"type\":\"done\",\"outPath\":%q}\n", outPath)
	fl.Flush()
}

// delete a specific version: removes manifests/{gid}/{ver}.json and content/{gid}/{ver}
func handleDeleteVersion(w http.ResponseWriter, r *http.Request) {
	gid := r.URL.Query().Get("gameId")
	ver := r.URL.Query().Get("version")
	if gid == "" || ver == "" {
		http.Error(w, "missing gameId or version", http.StatusBadRequest)
		return
	}
	if !isSafeGameID(gid) || !isSafeVersion(ver) {
		http.Error(w, "invalid gameId or version", http.StatusBadRequest)
		return
	}
	// remove manifest file
	manDir := filepath.Join(contentRoot, "manifests", gid)
	manPath := filepath.Join(manDir, ver+".json")
	if err := os.Remove(manPath); err != nil {
		if !os.IsNotExist(err) {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
	}
	// remove extracted content folder
	filesDir := filepath.Join(contentRoot, "content", gid, ver)
	_ = os.RemoveAll(filesDir)
	// adjust latest.json if it pointed to deleted version
	latestPath := filepath.Join(manDir, "latest.json")
	needRecalc := false
	if b, err := os.ReadFile(latestPath); err == nil {
		var m map[string]string
		if json.Unmarshal(b, &m) == nil {
			if strings.TrimSpace(m["version"]) == ver {
				needRecalc = true
			}
		}
	}
	if needRecalc {
		entries, _ := os.ReadDir(manDir)
		vers := make([]string, 0)
		for _, e := range entries {
			name := e.Name()
			if strings.EqualFold(name, "latest.json") {
				continue
			}
			if strings.HasSuffix(strings.ToLower(name), ".json") {
				vers = append(vers, strings.TrimSuffix(name, ".json"))
			}
		}
		sort.Slice(vers, func(i, j int) bool { return vers[i] < vers[j] })
		if len(vers) == 0 {
			// no versions remain: remove latest.json
			_ = os.Remove(latestPath)
		} else {
			newLatest := vers[len(vers)-1]
			b, _ := json.MarshalIndent(map[string]string{"version": newLatest}, "", "  ")
			_ = os.WriteFile(latestPath, b, 0o644)
		}
	}
	writeJSON(w, map[string]string{"status": "ok"})
}

// handleGameIconUpload saves uploaded image as manifests/{gameId}/icon.png and returns its URL
func handleGameIconUpload(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseMultipartForm(16 << 20); err != nil { // 16MB
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	gid := strings.TrimSpace(r.FormValue("gameId"))
	if gid == "" {
		http.Error(w, "missing gameId", http.StatusBadRequest)
		return
	}
	file, _, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file: "+err.Error(), http.StatusBadRequest)
		return
	}
	defer file.Close()
	data, err := io.ReadAll(file)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	// Decode using stdlib; support PNG/JPEG. For unsupported formats return 400.
	img, format, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		// try explicit decoders
		if im, e2 := png.Decode(bytes.NewReader(data)); e2 == nil {
			img = im
			format = "png"
		} else if im, e3 := jpeg.Decode(bytes.NewReader(data)); e3 == nil {
			img = im
			format = "jpeg"
		} else {
			http.Error(w, "unsupported image format", http.StatusBadRequest)
			return
		}
	}
	_ = format // currently unused; always encode PNG
	// Ensure directory and save as PNG with fixed name icon.png
	dir := filepath.Join(contentRoot, "manifests", gid)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	outPath := filepath.Join(dir, "icon.png")
	if !ensureWithin(filepath.Join(contentRoot, "manifests"), outPath) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	out, err := os.Create(outPath)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	defer out.Close()
	if err := png.Encode(out, img); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	url := "/manifests/" + gid + "/icon.png"
	writeJSON(w, map[string]string{"status": "ok", "url": url})
}

// meta storage for news: JSON file alongside index.json, keyed by slug
type newsMeta struct {
	Published bool   `json:"published"`
	CoverUrl  string `json:"coverUrl"`
}

func metaPath(base string) string { return filepath.Join(base, "news_meta.json") }

func readNewsMeta(base string) map[string]newsMeta {
	b, err := os.ReadFile(metaPath(base))
	if err != nil {
		return map[string]newsMeta{}
	}
	var m map[string]newsMeta
	if json.Unmarshal(b, &m) != nil || m == nil {
		return map[string]newsMeta{}
	}
	return m
}

func writeNewsMeta(base string, m map[string]newsMeta) error {
	b, _ := json.MarshalIndent(m, "", "  ")
	return os.WriteFile(metaPath(base), b, 0o644)
}

// processAndSaveAsset converts and saves image bytes into assets directory.
// - Chooses output extension and pipeline (static -> JPEG; animated GIF/WEBP -> WEBP if possible)
// - Resizes so that the minimal side is 1080 if larger
// - Returns final filename and optional meta fields (e.g., note, format)
func processAndSaveAsset(base, rel, desired string, data []byte, extHint, contentType string) (string, map[string]string, error) {
	meta := map[string]string{}
	ext := strings.ToLower(strings.TrimSpace(extHint))
	if ext == "" && contentType != "" {
		if strings.Contains(contentType, "png") {
			ext = ".png"
		} else if strings.Contains(contentType, "jpeg") || strings.Contains(contentType, "jpg") {
			ext = ".jpg"
		} else if strings.Contains(contentType, "gif") {
			ext = ".gif"
		} else if strings.Contains(contentType, "webp") {
			ext = ".webp"
		}
	}

	outExt := ".jpg"
	inAnimated := false
	switch ext {
	case ".png":
		outExt = ".jpg"
	case ".jpg", ".jpeg":
		outExt = ".jpg"
	case ".gif":
		outExt = ".webp"
		inAnimated = true
	case ".webp":
		outExt = ".webp"
		inAnimated = true
	default:
		if strings.Contains(strings.ToLower(contentType), "gif") {
			outExt = ".webp"
			inAnimated = true
		} else if strings.Contains(strings.ToLower(contentType), "webp") {
			outExt = ".webp"
			inAnimated = true
		} else {
			outExt = ".jpg"
		}
	}

	if strings.TrimSpace(desired) == "" {
		desired = "image"
	}
	outName := sanitizeFilename(desired) + outExt
	outDir := filepath.Join(base, rel)
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return "", nil, err
	}
	outPath := filepath.Join(outDir, outName)
	if !ensureWithin(base, outPath) {
		return "", nil, fmt.Errorf("invalid path")
	}

	if inAnimated {
		if hasFFmpeg() {
			tmpIn, err := os.CreateTemp("", "asset_in_*")
			if err != nil {
				return "", nil, err
			}
			if _, err := tmpIn.Write(data); err != nil {
				tmpIn.Close()
				return "", nil, err
			}
			tmpIn.Close()
			scaleExpr := "scale='if(gte(min(iw,ih),1080), if(lte(iw,ih), -2, 1080), iw)':'if(gte(min(iw,ih),1080), if(lte(iw,ih), 1080, -2), ih)'"
			// Use quality 95 for lossy output and encode with libwebp preserving animation
			// Add pixel format with alpha, preset and compression level for ffmpeg 6 compatibility
			args := []string{"-y", "-i", tmpIn.Name(), "-vf", scaleExpr,
				"-c:v", "libwebp", "-lossless", "0", "-q:v", "95", "-compression_level", "4",
				"-preset", "picture", "-pix_fmt", "yuva420p", "-vsync", "0", "-loop", "0", outPath}
			if err := runFFmpegTranscode(args); err != nil {
				os.Remove(tmpIn.Name())
				return "", nil, fmt.Errorf("ffmpeg failed: %w", err)
			}
			os.Remove(tmpIn.Name())
			return outName, meta, nil
		}
		// Fallback: keep original content and original extension to avoid misleading .webp name
		// Adjust output path/name to use input extension
		origExt := ext
		if origExt == "" {
			// try guess from contentType
			if strings.Contains(strings.ToLower(contentType), "gif") {
				origExt = ".gif"
			} else if strings.Contains(strings.ToLower(contentType), "webp") {
				origExt = ".webp"
			} else {
				origExt = ".gif"
			}
		}
		// Rebuild final path/name with original extension
		outName = sanitizeFilename(desired) + origExt
		outPath = filepath.Join(outDir, outName)
		if err := os.WriteFile(outPath, data, 0o644); err != nil {
			return "", nil, err
		}
		log.Printf("ffmpeg not found: saved original animated image as %s", outName)
		meta["note"] = "ffmpeg not found: saved original"
		return outName, meta, nil
	}

	img, format, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		switch ext {
		case ".png":
			if im, e2 := png.Decode(bytes.NewReader(data)); e2 == nil {
				img = im
				format = "png"
			} else {
				return "", nil, e2
			}
		case ".jpg", ".jpeg":
			if im, e2 := jpeg.Decode(bytes.NewReader(data)); e2 == nil {
				img = im
				format = "jpeg"
			} else {
				return "", nil, e2
			}
		default:
			return "", nil, fmt.Errorf("unsupported format")
		}
	}
	if format != "" {
		meta["format"] = format
	}
	outImg := resizeToMinSide1080(img)
	out, err := os.Create(outPath)
	if err != nil {
		return "", nil, err
	}
	defer out.Close()
	if err := jpeg.Encode(out, outImg, &jpeg.Options{Quality: 95}); err != nil {
		return "", nil, err
	}
	return outName, meta, nil
}

func ensureWithin(base, p string) bool {
	b, _ := filepath.Abs(base)
	q, _ := filepath.Abs(p)
	rel, err := filepath.Rel(b, q)
	if err != nil {
		return false
	}
	return !strings.HasPrefix(rel, "..") && rel != ""
}

func downloadURL(u string) ([]byte, string, error) {
	req, _ := http.NewRequest("GET", u, nil)
	req.Header.Set("User-Agent", "ChillHub-Admin/1.0")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return nil, "", err
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return nil, resp.Header.Get("Content-Type"), fmt.Errorf("http %d", resp.StatusCode)
	}
	// limit 50MB
	const max = 50 << 20
	var buf bytes.Buffer
	if _, err := io.CopyN(&buf, resp.Body, max); err != nil && err != io.EOF {
		return nil, resp.Header.Get("Content-Type"), err
	}
	return buf.Bytes(), resp.Header.Get("Content-Type"), nil
}

// handleNewsAssetsDelete deletes a file or directory in assets
func handleNewsAssetsDelete(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := filepath.Join(contentRoot, "news", "assets")
	rel := sanitizeAssetPath(r.FormValue("path"))
	name := sanitizeFilename(r.FormValue("name"))
	if name == "" {
		http.Error(w, "empty name", http.StatusBadRequest)
		return
	}
	target := filepath.Join(base, rel, name)
	if !ensureWithin(base, target) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	if err := os.RemoveAll(target); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]string{"status": "ok"})
}

// handleNewsRebuild triggers index.json generation
func handleNewsRebuild(w http.ResponseWriter, r *http.Request) {
	scope := r.URL.Query().Get("scope")
	gid := r.URL.Query().Get("gameId")
	base, err := newsBase(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if err := rebuildNewsIndex(base); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]string{"status": "ok"})
}

// rename file or directory
func handleNewsAssetsRename(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := filepath.Join(contentRoot, "news", "assets")
	rel := sanitizeAssetPath(r.FormValue("path"))
	from := sanitizeFilename(r.FormValue("from"))
	to := sanitizeFilename(r.FormValue("to"))
	if from == "" || to == "" {
		http.Error(w, "empty names", http.StatusBadRequest)
		return
	}
	src := filepath.Join(base, rel, from)
	dst := filepath.Join(base, rel, to)
	if !ensureWithin(base, src) || !ensureWithin(base, dst) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	if err := os.Rename(src, dst); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]string{"status": "ok"})
}

// upload by URL with processing similar to file upload
func handleNewsAssetsUploadByURL(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := filepath.Join(contentRoot, "news", "assets")
	rel := sanitizeAssetPath(r.FormValue("path"))
	desired := sanitizeFilename(r.FormValue("filename"))
	// strip extension if provided by client; processor will add proper extension
	if desired != "" {
		desired = strings.TrimSuffix(desired, filepath.Ext(desired))
	}
	srcURL := strings.TrimSpace(r.FormValue("url"))
	if srcURL == "" {
		http.Error(w, "empty url", http.StatusBadRequest)
		return
	}
	data, ct, err := downloadURL(srcURL)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	extHint := strings.ToLower(filepath.Ext(strings.Split(strings.Split(srcURL, "?")[0], "#")[0]))
	outName, meta, err := processAndSaveAsset(base, rel, desired, data, extHint, ct)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	url := "/assets/" + strings.TrimPrefix(filepath.ToSlash(filepath.Join(rel, outName)), "/")
	resp := map[string]string{"status": "ok", "url": url, "filename": outName}
	for k, v := range meta {
		resp[k] = v
	}
	writeJSON(w, resp)
}

// handleNewsAssetsList returns list of files from CONTENT_ROOT/news/assets for gallery
func handleNewsAssetsList(w http.ResponseWriter, r *http.Request) {
	base := filepath.Join(contentRoot, "news", "assets")
	rel := sanitizeAssetPath(r.URL.Query().Get("path"))
	dir := filepath.Join(base, rel)
	if !ensureWithin(base, dir) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		http.Error(w, err.Error(), http.StatusNotFound)
		return
	}
	q := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("q")))
	dirsOnly := r.URL.Query().Get("dirsOnly") == "1"
	type item struct {
		Name    string `json:"name"`
		URL     string `json:"url"`
		Size    int64  `json:"size"`
		ModTime string `json:"modTime"`
		IsDir   bool   `json:"isDir"`
	}
	out := struct {
		Path  string `json:"path"`
		Items []item `json:"items"`
	}{Path: filepath.ToSlash(rel), Items: []item{}}
	for _, e := range entries {
		if e.IsDir() {
			name := e.Name()
			if q != "" && !strings.Contains(strings.ToLower(name), q) {
				continue
			}
			out.Items = append(out.Items, item{Name: name, URL: "", Size: 0, ModTime: "", IsDir: true})
			continue
		}
		if dirsOnly {
			continue
		}
		name := e.Name()
		if q != "" && !strings.Contains(strings.ToLower(name), q) {
			continue
		}
		info, _ := e.Info()
		out.Items = append(out.Items, item{
			Name: name,
			URL:  "/assets/" + strings.TrimPrefix(filepath.ToSlash(filepath.Join(rel, name)), "/"),
			Size: func() int64 {
				if info != nil {
					return info.Size()
				}
				return 0
			}(),
			ModTime: func() string {
				if info != nil {
					return info.ModTime().UTC().Format(time.RFC3339)
				}
				return ""
			}(),
			IsDir: false,
		})
	}
	// sort by modTime desc
	sort.Slice(out.Items, func(i, j int) bool {
		if out.Items[i].IsDir != out.Items[j].IsDir {
			return out.Items[i].IsDir && !out.Items[j].IsDir
		}
		return out.Items[i].ModTime > out.Items[j].ModTime
	})
	writeJSON(w, out)
}

// mkdir for assets: POST path, name
func handleNewsAssetsMkdir(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := filepath.Join(contentRoot, "news", "assets")
	rel := sanitizeAssetPath(r.FormValue("path"))
	name := sanitizeFilename(r.FormValue("name"))
	if name == "" {
		http.Error(w, "empty name", http.StatusBadRequest)
		return
	}
	dir := filepath.Join(base, rel, name)
	if !ensureWithin(base, dir) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]string{"status": "ok"})
}

func handleNewsAssetsUpload(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseMultipartForm(64 << 20); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := filepath.Join(contentRoot, "news", "assets")
	rel := sanitizeAssetPath(r.FormValue("path"))
	desired := sanitizeFilename(r.FormValue("filename"))
	f, hdr, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file", http.StatusBadRequest)
		return
	}
	defer f.Close()
	data, err := io.ReadAll(f)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	inName := strings.ToLower(hdr.Filename)
	extHint := filepath.Ext(inName)
	if desired == "" {
		desired = strings.TrimSuffix(sanitizeFilename(hdr.Filename), extHint)
	} else {
		// strip extension if client provided it
		desired = strings.TrimSuffix(desired, filepath.Ext(desired))
	}
	outName, meta, err := processAndSaveAsset(base, rel, desired, data, extHint, "")
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	url := "/assets/" + strings.TrimPrefix(filepath.ToSlash(filepath.Join(rel, outName)), "/")
	resp := map[string]string{"status": "ok", "url": url, "filename": outName}
	for k, v := range meta {
		resp[k] = v
	}
	writeJSON(w, resp)
}

func sanitizeAssetPath(p string) string {
	p = filepath.ToSlash(strings.TrimSpace(p))
	p = strings.TrimPrefix(p, "/")
	p = strings.Trim(p, "/")
	p = strings.ReplaceAll(p, "..", "_")
	return p
}

func resizeToMinSide1080(img image.Image) image.Image {
	w := img.Bounds().Dx()
	h := img.Bounds().Dy()
	min := w
	if h < min {
		min = h
	}
	if min <= 1080 {
		return img
	}
	// scale so that min side becomes 1080
	scale := float64(1080) / float64(min)
	newW := int(float64(w) * scale)
	newH := int(float64(h) * scale)
	dst := image.NewRGBA(image.Rect(0, 0, newW, newH))
	// simple and dependency-free resizing using nearest neighbor sampling
	// note: for higher quality, switch to x/image/draw.ApproxBiLinear
	for y := 0; y < newH; y++ {
		for x := 0; x < newW; x++ {
			sx := int(float64(x) / scale)
			sy := int(float64(y) / scale)
			dst.Set(x, y, img.At(sx, sy))
		}
	}
	return dst
}

func ffmpegPath() string {
	if p := os.Getenv("FFMPEG_PATH"); strings.TrimSpace(p) != "" {
		return p
	}
	if p, err := exec.LookPath("ffmpeg"); err == nil {
		return p
	}
	if runtime.GOOS == "windows" {
		if p, err := exec.LookPath("ffmpeg.exe"); err == nil {
			return p
		}
	}
	return ""
}
func hasFFmpeg() bool { return ffmpegPath() != "" }
func runFFmpegTranscode(args []string) error {
	exe := ffmpegPath()
	if exe == "" {
		return fmt.Errorf("ffmpeg not found")
	}
	cmd := exec.Command(exe, args...)
	// Capture combined stdout/stderr to include in logs
	out, err := cmd.CombinedOutput()
	if err != nil {
		exitCode := 0
		if ee, ok := err.(*exec.ExitError); ok {
			exitCode = ee.ExitCode()
		}
		log.Printf("ffmpeg failed (code=%d) exe=%q args=%q output:\n%s", exitCode, exe, args, string(out))
		return fmt.Errorf("ffmpeg exited with code %d", exitCode)
	}
	log.Printf("ffmpeg succeeded exe=%q args=%q output:\n%s", exe, args, string(out))
	return nil
}

// noopFlusher is used as a fallback when the writer doesn't implement http.Flusher.
type noopFlusher struct{}

func (noopFlusher) Flush() {}

// NOTE: handleNextVersion and bumpPatch were removed as legacy (UI no longer uses next-version helper).

// list versions
func handleListVersions(w http.ResponseWriter, r *http.Request) {
	gid := r.URL.Query().Get("gameId")
	if gid == "" {
		http.Error(w, "missing gameId", http.StatusBadRequest)
		return
	}
	dir := filepath.Join(contentRoot, "manifests", gid)
	if st, err := os.Stat(dir); err != nil || !st.IsDir() {
		dir = filepath.Join(contentRoot, "content", "manifests", gid)
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		http.Error(w, err.Error(), http.StatusNotFound)
		return
	}
	type item struct {
		Version string `json:"version"`
	}
	out := struct {
		Items  []item `json:"items"`
		Latest string `json:"latest"`
	}{Items: []item{}, Latest: ""}
	for _, e := range entries {
		name := e.Name()
		if strings.EqualFold(name, "latest.json") {
			continue
		}
		if strings.HasSuffix(strings.ToLower(name), ".json") {
			out.Items = append(out.Items, item{Version: strings.TrimSuffix(name, ".json")})
		}
	}
	sort.Slice(out.Items, func(i, j int) bool { return out.Items[i].Version < out.Items[j].Version })
	// read latest.json if present
	lb, err := os.ReadFile(filepath.Join(dir, "latest.json"))
	if err == nil {
		var m map[string]string
		if json.Unmarshal(lb, &m) == nil {
			if v := strings.TrimSpace(m["version"]); v != "" {
				out.Latest = v
			}
		}
	}
	writeJSON(w, out)
}

func handleActivate(w http.ResponseWriter, r *http.Request) {
	gid := r.URL.Query().Get("gameId")
	ver := r.URL.Query().Get("version")
	if gid == "" || ver == "" {
		http.Error(w, "missing gameId or version", http.StatusBadRequest)
		return
	}
	dir := filepath.Join(contentRoot, "manifests", gid)
	if _, err := os.Stat(filepath.Join(dir, ver+".json")); err != nil {
		dir = filepath.Join(contentRoot, "content", "manifests", gid)
		if _, err2 := os.Stat(filepath.Join(dir, ver+".json")); err2 != nil {
			http.Error(w, "version manifest not found", http.StatusNotFound)
			return
		}
	}
	latest := map[string]string{"version": ver}
	b, _ := json.MarshalIndent(latest, "", "  ")
	if err := os.WriteFile(filepath.Join(dir, "latest.json"), b, 0o644); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// generateGamesFromManifests scans content/manifests/* and creates initial list
func generateGamesFromManifests() []gameEntry {
	base := filepath.Join(contentRoot, "manifests")
	entries, err := os.ReadDir(base)
	if err != nil {
		return []gameEntry{}
	}
	items := make([]gameEntry, 0)
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		gid := e.Name()
		name := strings.ToLower(gid)
		// Skip special/system folders that are not games
		if name == "repo" || name == "_registry" || name == "launcher" {
			continue
		}
		items = append(items, gameEntry{GameID: gid, Title: gid, ExeRelativePath: "", IconURL: ""})
	}
	sort.Slice(items, func(i, j int) bool { return items[i].GameID < items[j].GameID })
	return items
}

// games registry
type gameEntry struct {
	GameID          string `json:"gameId"`
	Title           string `json:"title"`
	ExeRelativePath string `json:"exeRelativePath"`
	IconURL         string `json:"iconUrl"`
}

func gamesRegistryPath() string {
	// Store registry separately from any game ID to avoid collisions
	return filepath.Join(contentRoot, "manifests", "_registry", "games.json")
}

func handleGamesGet(w http.ResponseWriter, r *http.Request) {
	p := gamesRegistryPath()
	if _, err := os.Stat(p); err != nil {
		// Autogenerate from manifests/{gameId}/ directories (exclude 'launcher')
		items := generateGamesFromManifests()
		outDir := filepath.Dir(p)
		_ = os.MkdirAll(outDir, 0o755)
		b, _ := json.MarshalIndent(struct {
			Items []gameEntry `json:"items"`
		}{Items: items}, "", "  ")
		_ = os.WriteFile(p, b, 0o644)
		w.Header().Set("Content-Type", "application/json")
		w.Write(b)
		return
	}
	b, err := os.ReadFile(p)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

func handleGamesSave(w http.ResponseWriter, r *http.Request) {
	var payload struct {
		Items []gameEntry `json:"items"`
	}
	if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	outDir := filepath.Dir(gamesRegistryPath())
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	b, _ := json.MarshalIndent(payload, "", "  ")
	if err := os.WriteFile(gamesRegistryPath(), b, 0o644); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// sanitizeFilename keeps only safe characters for filenames
func sanitizeFilename(name string) string {
	safe := make([]rune, 0, len(name))
	for _, r := range name {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '.' || r == '-' || r == '_' {
			safe = append(safe, r)
		} else {
			safe = append(safe, '_')
		}
	}
	if len(safe) == 0 {
		return "file"
	}
	return string(safe)
}

// handleNewsUploadCover saves uploaded image into content/news/assets and returns coverUrl
func handleNewsUploadCover(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseMultipartForm(32 << 20); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	file, hdr, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file: "+err.Error(), http.StatusBadRequest)
		return
	}
	defer file.Close()
	base := filepath.Join(contentRoot, "news", "assets")
	if err := os.MkdirAll(base, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	name := sanitizeFilename(hdr.Filename)
	outPath := filepath.Join(base, name)
	out, err := os.Create(outPath)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	if _, err := io.Copy(out, file); err != nil {
		out.Close()
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	out.Close()
	// Return a web path expected by the client/launcher
	url := "/assets/" + name
	// Optionally update meta if scope+slug provided
	scope := r.FormValue("scope")
	gid := r.FormValue("gameId")
	slug := r.FormValue("slug")
	if strings.TrimSpace(slug) != "" {
		if baseDir, err := newsBase(scope, gid); err == nil {
			m := readNewsMeta(baseDir)
			cur := m[slug]
			cur.CoverUrl = url
			m[slug] = cur
			_ = writeNewsMeta(baseDir, m)
			_ = rebuildNewsIndex(baseDir)
		}
	}
	writeJSON(w, map[string]string{"coverUrl": url})
}

// ===== News management =====

// resolve news base directory by scope and optional gameId
func newsBase(scope, gid string) (string, error) {
	if scope == "launcher" {
		return filepath.Join(contentRoot, "news"), nil
	}
	if scope == "game" {
		if gid == "" {
			return "", fmt.Errorf("gameId required for scope=game")
		}
		return filepath.Join(contentRoot, "news", "games", gid), nil
	}
	return "", fmt.Errorf("invalid scope: %s", scope)
}

// handleNewsList returns index.json for scope
func handleNewsList(w http.ResponseWriter, r *http.Request) {
	scope := r.URL.Query().Get("scope")
	gid := r.URL.Query().Get("gameId")
	base, err := newsBase(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	idxPath := filepath.Join(base, "index.json")
	b, err := os.ReadFile(idxPath)
	if err != nil {
		http.Error(w, err.Error(), http.StatusNotFound)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// handleNewsGet returns markdown for slug
func handleNewsGet(w http.ResponseWriter, r *http.Request) {
	scope := r.URL.Query().Get("scope")
	gid := r.URL.Query().Get("gameId")
	slug := r.URL.Query().Get("slug")
	if slug == "" {
		http.Error(w, "missing slug", http.StatusBadRequest)
		return
	}
	base, err := newsBase(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	p := filepath.Join(base, slug+".md")
	b, err := os.ReadFile(p)
	if err != nil {
		http.Error(w, err.Error(), http.StatusNotFound)
		return
	}
	meta := readNewsMeta(base)[slug]
	w.Header().Set("Content-Type", "application/json")
	writeJSON(w, map[string]any{"markdown": string(b), "published": meta.Published, "coverUrl": meta.CoverUrl})
}

// handleNewsSave saves markdown for slug and optionally rebuilds index
func handleNewsSave(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseMultipartForm(16 << 20); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	scope := r.FormValue("scope")
	gid := r.FormValue("gameId")
	slug := r.FormValue("slug")
	md := r.FormValue("markdown")
	cov := strings.TrimSpace(r.FormValue("coverUrl"))
	pubStr := strings.TrimSpace(strings.ToLower(r.FormValue("published")))
	pub := pubStr == "true" || pubStr == "1" || pubStr == "yes"
	if slug == "" {
		http.Error(w, "missing slug", http.StatusBadRequest)
		return
	}
	base, err := newsBase(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if err := os.MkdirAll(base, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	p := filepath.Join(base, slug+".md")
	if err := os.WriteFile(p, []byte(md), 0o644); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	// update meta if provided
	m := readNewsMeta(base)
	cur := m[slug]
	if cov != "" {
		cur.CoverUrl = cov
	}
	// honor explicit published flag in save (if not provided, leave as-is)
	if r.Form.Has("published") {
		cur.Published = pub
	}
	m[slug] = cur
	_ = writeNewsMeta(base, m)
	if err := rebuildNewsIndex(base); err != nil {
		http.Error(w, "saved but index rebuild failed: "+err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]string{"status": "ok", "slug": slug})
}

// handleNewsDelete deletes markdown and removes meta entry, then rebuilds index
func handleNewsDelete(w http.ResponseWriter, r *http.Request) {
	scope := r.URL.Query().Get("scope")
	gid := r.URL.Query().Get("gameId")
	slug := r.URL.Query().Get("slug")
	if strings.TrimSpace(slug) == "" {
		http.Error(w, "missing slug", http.StatusBadRequest)
		return
	}
	base, err := newsBase(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	p := filepath.Join(base, slug+".md")
	if err := os.Remove(p); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	// remove meta entry if exists
	m := readNewsMeta(base)
	delete(m, slug)
	_ = writeNewsMeta(base, m)
	if err := rebuildNewsIndex(base); err != nil {
		http.Error(w, "deleted but index rebuild failed: "+err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]string{"status": "ok"})
}

// handleNewsPublish toggles published flag in meta and rebuilds index
func handleNewsPublish(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	scope := r.FormValue("scope")
	gid := r.FormValue("gameId")
	slug := r.FormValue("slug")
	pubStr := strings.TrimSpace(strings.ToLower(r.FormValue("published")))
	pub := pubStr == "true" || pubStr == "1" || pubStr == "yes"
	if strings.TrimSpace(slug) == "" {
		http.Error(w, "missing slug", http.StatusBadRequest)
		return
	}
	base, err := newsBase(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	m := readNewsMeta(base)
	cur := m[slug]
	cur.Published = pub
	m[slug] = cur
	if err := writeNewsMeta(base, m); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	if err := rebuildNewsIndex(base); err != nil {
		http.Error(w, "updated but index rebuild failed: "+err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]any{"status": "ok", "slug": slug, "published": pub})
}

func handleNewsPreview(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseMultipartForm(4 << 20); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	md := r.FormValue("markdown")
	// Build a compact "card" preview similar to client list: cover (first image), title, summary
	title, summary, cover := extractMeta(md)
	card := "<div class=\"card\" style=\"max-width:680px\">"
	if strings.TrimSpace(cover) != "" {
		card += "<img src=\"" + cover + "\" class=\"card-img-top\" alt=\"cover\" style=\"height:160px;object-fit:cover\">"
	}
	card += "<div class=\"card-body\">"
	if strings.TrimSpace(title) != "" {
		card += "<h5 class=\"card-title mb-1\">" + inlineMD(title) + "</h5>"
	}
	if strings.TrimSpace(summary) != "" {
		card += "<p class=\"card-text text-body-secondary\">" + inlineMD(summary) + "</p>"
	}
	card += "</div></div>"
	contentHTML := mdToHTML(md)
	w.Header().Set("Content-Type", "application/json")
	writeJSON(w, map[string]string{"listHtml": card, "contentHtml": contentHTML})
}

func rebuildNewsIndex(base string) error {
	entries, err := os.ReadDir(base)
	if err != nil {
		return err
	}
	meta := readNewsMeta(base)
	type item struct {
		Id        string `json:"id"`
		Title     string `json:"title"`
		Slug      string `json:"slug"`
		CreatedAt string `json:"createdAt"`
		Summary   string `json:"summary"`
		CoverUrl  string `json:"coverUrl"`
		Published bool   `json:"published"`
	}
	var items []item
	for _, e := range entries {
		name := e.Name()
		if !strings.HasSuffix(strings.ToLower(name), ".md") {
			continue
		}
		slug := strings.TrimSuffix(name, ".md")
		p := filepath.Join(base, name)
		b, err := os.ReadFile(p)
		if err != nil {
			continue
		}
		body := string(b)
		// compute content-based fields
		t, s, cFromBody := extractMeta(body)
		// take cover and published strictly from meta file; do not infer on rebuild
		metaEntry, ok := meta[slug]
		c := ""
		pub := false
		if ok {
			c = metaEntry.CoverUrl
			pub = metaEntry.Published
		} else {
			// keep index consistent even if meta entry missing
			c = cFromBody
			pub = false
		}
		st, _ := os.Stat(p)
		created := time.Now().UTC().Format(time.RFC3339)
		if st != nil {
			created = st.ModTime().UTC().Format(time.RFC3339)
		}
		items = append(items, item{
			Id:        slug,
			Title:     t,
			Slug:      slug,
			CreatedAt: created,
			Summary:   s,
			CoverUrl:  c,
			Published: pub,
		})
	}
	sort.Slice(items, func(i, j int) bool { return items[i].CreatedAt > items[j].CreatedAt })
	out := struct {
		Items []item `json:"items"`
	}{Items: items}
	b, _ := json.MarshalIndent(out, "", "  ")
	if err := os.WriteFile(filepath.Join(base, "index.json"), b, 0o644); err != nil {
		return err
	}
	// do not mutate meta during rebuild
	return nil
}

func extractMeta(md string) (string, string, string) {
	lines := strings.Split(md, "\n")
	title := ""
	cover := ""
	var paras []string
	cur := ""
	for _, ln := range lines {
		s := strings.TrimRight(ln, "\r")
		// first image ![alt](url) if cover not set yet
		if cover == "" {
			ts2 := strings.TrimSpace(s)
			if i := strings.Index(ts2, "!["); i >= 0 {
				j := strings.Index(ts2[i:], "](")
				if j >= 0 {
					j = i + j
					k := strings.Index(ts2[j+2:], ")")
					if k >= 0 {
						k = j + 2 + k
						url := normalize(strings.TrimSpace(ts2[j+2 : k]))
						if url != "" {
							cover = url
						}
					}
				}
			}
		}
		if strings.HasPrefix(s, "# ") && title == "" {
			title = strings.TrimSpace(strings.TrimPrefix(s, "# "))
			continue
		}
		if strings.TrimSpace(s) == "" {
			if strings.TrimSpace(cur) != "" {
				paras = append(paras, strings.TrimSpace(cur))
				cur = ""
			}
		} else {
			if cur != "" {
				cur += "\n"
			}
			cur += s
		}
	}
	if strings.TrimSpace(cur) != "" {
		paras = append(paras, strings.TrimSpace(cur))
	}
	summary := ""
	if len(paras) > 0 {
		summary = paras[0]
	}
	return title, summary, cover
}

func normalize(u string) string {
	u = strings.TrimSpace(u)
	if u == "" {
		return u
	}
	if strings.HasPrefix(u, "http://") || strings.HasPrefix(u, "https://") {
		return u
	}
	u = strings.TrimPrefix(u, "./")
	if strings.HasPrefix(u, "/") {
		return u
	}
	if strings.HasPrefix(u, "assets/") {
		return "/assets/" + strings.TrimPrefix(u, "assets/")
	}
	// default: treat as /assets/<u>
	return "/assets/" + u
}

// very small markdown to HTML for preview (H1/H2, paragraphs, code blocks, links, bold/italic)
func mdToHTML(md string) string {
	esc := func(s string) string {
		s = strings.ReplaceAll(s, "&", "&amp;")
		s = strings.ReplaceAll(s, "<", "&lt;")
		s = strings.ReplaceAll(s, ">", "&gt;")
		return s
	}
	// code blocks ```
	out := ""
	lines := strings.Split(md, "\n")
	inCode := false
	para := ""
	flushPara := func() {
		if strings.TrimSpace(para) != "" {
			out += "<p>" + inlineMD(esc(para)) + "</p>\n"
		}
		para = ""
	}
	for _, ln := range lines {
		if strings.HasPrefix(strings.TrimSpace(ln), "```") {
			if inCode {
				out += "</pre>\n"
				inCode = false
			} else {
				flushPara()
				out += "<pre>"
				inCode = true
			}
			continue
		}
		if inCode {
			out += esc(ln) + "\n"
			continue
		}
		s := strings.TrimRight(ln, "\r")
		if strings.HasPrefix(s, "# ") {
			flushPara()
			out += "<h1>" + inlineMD(esc(strings.TrimSpace(strings.TrimPrefix(s, "# ")))) + "</h1>\n"
			continue
		}
		if strings.HasPrefix(s, "## ") {
			flushPara()
			out += "<h2>" + inlineMD(esc(strings.TrimSpace(strings.TrimPrefix(s, "## ")))) + "</h2>\n"
			continue
		}
		if strings.TrimSpace(s) == "" {
			flushPara()
			continue
		}
		if para != "" {
			para += "\n"
		}
		para += s
	}
	if strings.TrimSpace(para) != "" {
		out += "<p>" + inlineMD(esc(para)) + "</p>\n"
	}
	return out
}

// inlineMD: very small subset (**bold**, *italic*, [text](url))
func inlineMD(s string) string {
	// images ![alt](url)
	for {
		i := strings.Index(s, "![")
		if i < 0 {
			break
		}
		j := strings.Index(s[i:], "](")
		if j < 0 {
			break
		}
		j = i + j
		k := strings.Index(s[j+2:], ")")
		if k < 0 {
			break
		}
		k = j + 2 + k
		alt := s[i+2 : j]
		url := normalize(s[j+2 : k])
		rep := "<img src=\"" + url + "\" alt=\"" + alt + "\" style=\"max-width:100%\">"
		s = s[:i] + rep + s[k+1:]
	}
	// bold **text**
	s = strings.ReplaceAll(s, "**", "\x00")
	parts := strings.Split(s, "\x00")
	for i := 1; i < len(parts); i += 2 {
		parts[i] = "<strong>" + parts[i] + "</strong>"
	}
	s = strings.Join(parts, "")
	// italic *text*
	s = strings.ReplaceAll(s, "*", "\x01")
	parts = strings.Split(s, "\x01")
	for i := 1; i < len(parts); i += 2 {
		parts[i] = "<em>" + parts[i] + "</em>"
	}
	s = strings.Join(parts, "")
	// links [text](url) (very naive)
	for {
		i := strings.Index(s, "[")
		if i < 0 {
			break
		}
		j := strings.Index(s[i:], "](")
		if j < 0 {
			break
		}
		j = i + j
		k := strings.Index(s[j+2:], ")")
		if k < 0 {
			break
		}
		k = j + 2 + k
		text := s[i+1 : j]
		url := normalize(s[j+2 : k])
		rep := "<a href=\"" + url + "\" target=\"_blank\">" + text + "</a>"
		s = s[:i] + rep + s[k+1:]
	}
	return s
}

func handleSystemFreeSpace(w http.ResponseWriter, r *http.Request) {
	base := contentRoot
	if strings.TrimSpace(base) == "" {
		base = "."
	}
	// Prefer getting both free and total where supported
	var free, total uint64
	if f, t, err := getDiskSpaceImpl(base); err == nil {
		free, total = f, t
	} else if f2, err2 := getFreeSpaceBytes(base); err2 == nil {
		free = f2
		total = 0
	} else {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]any{
		"path":  base,
		"bytes": free,
		"total": total,
	})
}

func main() {
	http.HandleFunc("/admin/health", func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprintln(w, "ok")
	})
	// Admin UI entry points
	http.HandleFunc("/admin/", handleAdminUI)
	http.HandleFunc("/admin", func(w http.ResponseWriter, r *http.Request) {
		// Normalize to trailing slash for relative asset links
		http.Redirect(w, r, "/admin/", http.StatusFound)
	})
	http.HandleFunc("/admin/api/auth/login", handleAuthLogin)
	http.HandleFunc("/admin/api/auth/logout", handleAuthLogout)
	http.HandleFunc("/admin/api/auth/refresh", handleAuthRefresh)
	http.HandleFunc("/admin/api/auth/me", handleAuthMe)
	http.HandleFunc("/admin/api/auth/verify", handleAuthVerify)
	http.HandleFunc("/admin/api", handleAdminUI)
	http.HandleFunc("/admin/api/list", handleListVersions)
	http.HandleFunc("/admin/api/activate", handleActivate)
	http.HandleFunc("/admin/api/deleteVersion", handleDeleteVersion)
	http.HandleFunc("/admin/api/upload", handleUpload)
	http.HandleFunc("/admin/api/uploadStream", handleUploadStream)
	http.HandleFunc("/admin/list", handleListVersions)
	http.HandleFunc("/admin/activate", handleActivate)
	http.HandleFunc("/admin/deleteVersion", handleDeleteVersion)
	// Public feedback submit (no auth)
	http.HandleFunc("/feedback/submit", rateLimitFeedbackSubmit(handleFeedbackSubmit))

	// Feedback admin endpoints (mirrored under /admin and /admin/api)
	http.HandleFunc("/admin/feedback/list", handleFeedbackList)
	http.HandleFunc("/admin/feedback/get", handleFeedbackGet)
	http.HandleFunc("/admin/feedback/delete", handleFeedbackDelete)
	http.HandleFunc("/admin/feedback/toggleImportant", handleFeedbackToggleImportant)
	http.HandleFunc("/admin/feedback/markRead", handleFeedbackMarkRead)
	http.HandleFunc("/admin/feedback/clear", handleFeedbackClear)
	http.HandleFunc("/admin/api/feedback/list", handleFeedbackList)
	http.HandleFunc("/admin/api/feedback/get", handleFeedbackGet)
	http.HandleFunc("/admin/feedback/markUnread", handleFeedbackMarkUnread)
	http.HandleFunc("/admin/api/feedback/markUnread", handleFeedbackMarkUnread)
	http.HandleFunc("/admin/api/feedback/delete", handleFeedbackDelete)
	http.HandleFunc("/admin/api/feedback/toggleImportant", handleFeedbackToggleImportant)
	http.HandleFunc("/admin/api/feedback/markRead", handleFeedbackMarkRead)
	http.HandleFunc("/admin/api/feedback/clear", handleFeedbackClear)

	// News management
	http.HandleFunc("/admin/news/list", handleNewsList)
	http.HandleFunc("/admin/news/get", handleNewsGet)
	http.HandleFunc("/admin/news/save", handleNewsSave)
	http.HandleFunc("/admin/news/delete", handleNewsDelete)
	http.HandleFunc("/admin/news/rebuild", handleNewsRebuild)
	http.HandleFunc("/admin/news/publish", handleNewsPublish)
	http.HandleFunc("/admin/news/preview", handleNewsPreview)
	http.HandleFunc("/admin/news/uploadCover", handleNewsUploadCover)
	// Upload builds (ZIP) for launcher/game
	http.HandleFunc("/admin/upload", handleUpload)
	// Streaming variant: upload ZIP and stream status as NDJSON
	http.HandleFunc("/admin/uploadStream", handleUploadStream)

	// === Mirror routes under /admin/api/* for nginx proxy ===
	http.HandleFunc("/admin/api/health", func(w http.ResponseWriter, r *http.Request) { fmt.Fprintln(w, "ok") })
	// Core endpoints already registered above; avoid duplicate registrations that panic on net/http ServeMux
	// http.HandleFunc("/admin/api", handleAdminUI)
	// http.HandleFunc("/admin/api/list", handleListVersions)
	// http.HandleFunc("/admin/api/activate", handleActivate)
	// http.HandleFunc("/admin/api/deleteVersion", handleDeleteVersion)
	// http.HandleFunc("/admin/api/upload", handleUpload)
	// http.HandleFunc("/admin/api/uploadStream", handleUploadStream)
	// News management
	http.HandleFunc("/admin/api/news/list", handleNewsList)
	http.HandleFunc("/admin/api/news/get", handleNewsGet)
	http.HandleFunc("/admin/api/news/save", handleNewsSave)
	http.HandleFunc("/admin/api/news/delete", handleNewsDelete)
	http.HandleFunc("/admin/api/news/rebuild", handleNewsRebuild)
	http.HandleFunc("/admin/api/news/publish", handleNewsPublish)
	http.HandleFunc("/admin/api/news/preview", handleNewsPreview)
	// System info
	http.HandleFunc("/admin/api/system/free", handleSystemFreeSpace)
	http.HandleFunc("/admin/api/news/uploadCover", handleNewsUploadCover)
	http.HandleFunc("/admin/api/news/assets", handleNewsAssetsList)
	http.HandleFunc("/admin/api/news/assets/mkdir", handleNewsAssetsMkdir)
	http.HandleFunc("/admin/api/news/assets/upload", handleNewsAssetsUpload)
	http.HandleFunc("/admin/api/news/assets/uploadByUrl", handleNewsAssetsUploadByURL)
	http.HandleFunc("/admin/api/news/assets/delete", handleNewsAssetsDelete)
	http.HandleFunc("/admin/api/news/assets/rename", handleNewsAssetsRename)
	// Games registry under /admin/api
	http.HandleFunc("/admin/api/games", handleGamesGet)
	http.HandleFunc("/admin/api/games/save", handleGamesSave)
	http.HandleFunc("/admin/api/games/icon/upload", handleGameIconUpload)
	http.HandleFunc("/admin/api/games/scan", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, struct {
			Items []gameEntry `json:"items"`
		}{Items: generateGamesFromManifests()})
	})
	// Assets gallery
	http.HandleFunc("/admin/news/assets", handleNewsAssetsList)
	http.HandleFunc("/admin/news/assets/mkdir", handleNewsAssetsMkdir)
	http.HandleFunc("/admin/news/assets/upload", handleNewsAssetsUpload)
	http.HandleFunc("/admin/news/assets/uploadByUrl", handleNewsAssetsUploadByURL)
	http.HandleFunc("/admin/news/assets/delete", handleNewsAssetsDelete)
	http.HandleFunc("/admin/news/assets/rename", handleNewsAssetsRename)
	// Mirror non-API system endpoint for environments without nginx rewrite
	http.HandleFunc("/admin/system/free", handleSystemFreeSpace)

	// Serve static news and assets so that admin UI can display images without external nginx
	newsDir := filepath.Join(contentRoot, "news")
	if st, err := os.Stat(newsDir); err == nil && st.IsDir() {
		http.Handle("/news/", httpx.NoStore(http.StripPrefix("/news/", http.FileServer(http.Dir(newsDir)))))
	}
	assetsDir := filepath.Join(newsDir, "assets")
	if st2, err2 := os.Stat(assetsDir); err2 == nil && st2.IsDir() {
		http.Handle("/assets/", httpx.NoStore(http.StripPrefix("/assets/", http.FileServer(http.Dir(assetsDir)))))
	}
	// Serve manifests for Admin UI (latest.json and version manifests)
	manifestsDir := filepath.Join(contentRoot, "manifests")
	if st3, err3 := os.Stat(manifestsDir); err3 == nil && st3.IsDir() {
		http.Handle("/manifests/", httpx.NoStore(http.StripPrefix("/manifests/", http.FileServer(http.Dir(manifestsDir)))))
	}
	// Serve static Admin UI assets from server/admin_ui
	uiDir := detectAdminUIDir()
	if st, err := os.Stat(uiDir); err == nil && st.IsDir() {
		http.Handle("/admin/ui/", httpx.NoStore(http.StripPrefix("/admin/ui/", http.FileServer(http.Dir(uiDir)))))
	}
	// Games registry
	http.HandleFunc("/admin/games", handleGamesGet)
	http.HandleFunc("/admin/games/save", handleGamesSave)
	http.HandleFunc("/admin/games/icon/upload", handleGameIconUpload)
	http.HandleFunc("/admin/games/scan", func(w http.ResponseWriter, r *http.Request) {
		writeJSON(w, struct {
			Items []gameEntry `json:"items"`
		}{Items: generateGamesFromManifests()})
	})
	// Заглушки эндпоинтов: upload/activate/rollback/list, news/save
	// Реализация будет добавлена на следующем шаге.
	addr := ":55777"
	log.Printf("admin API listening on %s (CONTENT_ROOT=%s)", addr, contentRoot)
	// Middlewares: RequestID -> CORS -> Auth -> Logging
	var h http.Handler = http.DefaultServeMux
	h = httpx.RequestID()(h)
	h = httpx.CORS("*")(h)
	h = adminAuthMiddleware(h)
	h = httpx.Logging("ADMIN")(h)
	log.Fatal(http.ListenAndServe(addr, h))
}

func handleAdminUI(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	// If not authenticated, serve login page; otherwise serve admin dashboard
	_, user := currentUser(r)
	uiDir := detectAdminUIDir()
	if user == "" {
		http.ServeFile(w, r, filepath.Join(uiDir, "login.html"))
		return
	}
	http.ServeFile(w, r, filepath.Join(uiDir, "admin.html"))
}

func detectAdminUIDir() string {
	// 1) alongside executable: ../server/admin_ui or ./admin_ui
	if exe, err := os.Executable(); err == nil && exe != "" {
		d := filepath.Dir(exe)
		// try ./admin_ui relative to exe
		p1 := filepath.Join(d, "admin_ui")
		if st, err := os.Stat(p1); err == nil && st.IsDir() {
			return p1
		}
		// try ../server/admin_ui (dev run from server/cmd/admin/...)
		p2 := filepath.Clean(filepath.Join(d, "..", "..", "admin_ui"))
		if st, err := os.Stat(p2); err == nil && st.IsDir() {
			return p2
		}
		// try ../../server/admin_ui
		p3 := filepath.Clean(filepath.Join(d, "..", "admin_ui"))
		if st, err := os.Stat(p3); err == nil && st.IsDir() {
			return p3
		}
	}
	// 2) fallback: walk up to 6 levels from working directory and try server/admin_ui and admin_ui
	wd, _ := os.Getwd()
	cur := wd
	for i := 0; i < 6; i++ {
		cand1 := filepath.Join(cur, "server", "admin_ui")
		if st, err := os.Stat(cand1); err == nil && st.IsDir() {
			return cand1
		}
		cand2 := filepath.Join(cur, "admin_ui")
		if st, err := os.Stat(cand2); err == nil && st.IsDir() {
			return cand2
		}
		parent := filepath.Dir(cur)
		if parent == cur {
			break
		}
		cur = parent
	}
	return "server/admin_ui"
}

func writeJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	b, _ := json.Marshal(v)
	w.Write(b)
}

type manifest struct {
	Version   string         `json:"version"`
	BuildID   string         `json:"buildId"`
	GameID    string         `json:"gameId"`
	CreatedAt string         `json:"createdAt"`
	Files     []manifestFile `json:"files"`
	EmptyDirs []string       `json:"emptyDirs"`
	Signature string         `json:"signature"`
}

type manifestFile struct {
	Path       string `json:"path"`
	Size       int64  `json:"size"`
	Blake3     string `json:"blake3"`
	Sha256     string `json:"sha256,omitempty"`
	Executable bool   `json:"executable"`
}

func ensureTrailingSlash(s string) string {
	if s == "" {
		return s
	}
	if !strings.HasSuffix(s, "/") {
		return s + "/"
	}
	return s
}

func isExecutable(rel string) bool {
	// simple heuristic for Windows builds
	return strings.HasSuffix(strings.ToLower(rel), ".exe")
}

func newBuildID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return fmt.Sprintf("build-%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(b[:])
}

// Allow only [A-Za-z0-9_-] for game IDs and not empty
func isSafeGameID(s string) bool {
	if strings.TrimSpace(s) == "" {
		return false
	}
	for _, r := range s {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '-' || r == '_' {
			continue
		}
		return false
	}
	return true
}

// Allow [0-9A-Za-z._-] for version labels (e.g., semver with pre-release), not empty
func isSafeVersion(s string) bool {
	if strings.TrimSpace(s) == "" {
		return false
	}
	for _, r := range s {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '-' || r == '_' || r == '.' {
			continue
		}
		return false
	}
	return true
}

// Handle ZIP upload and publish a release (launcher or game)
func handleUpload(w http.ResponseWriter, r *http.Request) {
	if err := r.ParseMultipartForm(1 << 30); err != nil { // up to 1GB form parsing window
		http.Error(w, "multipart parse error: "+err.Error(), http.StatusBadRequest)
		return
	}
	kind := r.FormValue("kind")
	gid := r.FormValue("gameId")
	ver := r.FormValue("version")
	upd := r.FormValue("updateLatest") == "1"
	if kind == "" {
		http.Error(w, "missing kind (launcher|game)", http.StatusBadRequest)
		return
	}
	if ver == "" {
		http.Error(w, "missing version", http.StatusBadRequest)
		return
	}
	if kind == "game" && gid == "" {
		http.Error(w, "missing gameId for kind=game", http.StatusBadRequest)
		return
	}
	if kind == "launcher" {
		gid = "launcher"
	}
	// validate inputs
	if !isSafeGameID(gid) {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return
	}
	if !isSafeVersion(ver) {
		http.Error(w, "invalid version", http.StatusBadRequest)
		return
	}

	f, hdr, err := r.FormFile("zip")
	if err != nil {
		http.Error(w, "missing zip: "+err.Error(), http.StatusBadRequest)
		return
	}
	defer f.Close()
	log.Printf("/admin/upload: kind=%s gid=%s ver=%s zip=%s", kind, gid, ver, hdr.Filename)

	// Where to extract files
	filesRoot := filepath.Join(contentRoot, "content", gid, ver, "files")
	if err := os.MkdirAll(filesRoot, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}

	// Save zip to temp and extract
	tmpZip, err := os.CreateTemp("", "upload-*.zip")
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	tmpName := tmpZip.Name()
	if _, err := io.Copy(tmpZip, f); err != nil {
		tmpZip.Close()
		os.Remove(tmpName)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	tmpZip.Close()
	defer os.Remove(tmpName)

	if err := unzipTo(tmpName, filesRoot); err != nil {
		http.Error(w, "unzip failed: "+err.Error(), http.StatusInternalServerError)
		return
	}

	// Build manifest by scanning extracted files
	var files []manifestFile
	dirHasFile := map[string]bool{}
	allDirs := map[string]bool{}
	err = filepath.WalkDir(filesRoot, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(filesRoot, path)
		rel = filepath.ToSlash(rel)
		if d.IsDir() {
			allDirs[rel] = true
			return nil
		}
		if rel == "." {
			return nil
		}
		info, _ := d.Info()
		f, err := os.Open(path)
		if err != nil {
			return err
		}
		hSha := sha256.New()
		hB3 := blake3.New()
		if _, err := io.Copy(io.MultiWriter(hSha, hB3), f); err != nil {
			f.Close()
			return err
		}
		f.Close()
		files = append(files, manifestFile{
			Path:       rel,
			Size:       info.Size(),
			Blake3:     hex.EncodeToString(hB3.Sum(nil)),
			Sha256:     hex.EncodeToString(hSha.Sum(nil)),
			Executable: isExecutable(rel),
		})
		p := filepath.ToSlash(filepath.Dir(rel))
		for p != "." && p != "/" {
			dirHasFile[p] = true
			p = filepath.ToSlash(filepath.Dir(p))
		}
		return nil
	})
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	emptyDirs := make([]string, 0)
	for d := range allDirs {
		if d == "." || d == "" {
			continue
		}
		if !dirHasFile[d] {
			emptyDirs = append(emptyDirs, ensureTrailingSlash(d))
		}
	}
	sort.Strings(emptyDirs)

	m := manifest{
		Version:   ver,
		BuildID:   newBuildID(),
		GameID:    gid,
		CreatedAt: time.Now().UTC().Format(time.RFC3339),
		Files:     files,
		EmptyDirs: emptyDirs,
		Signature: "dev-mock-signature",
	}
	outDir := filepath.Join(contentRoot, "manifests", gid)
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	outPath := filepath.Join(outDir, ver+".json")
	b, _ := json.MarshalIndent(m, "", "  ")
	if err := os.WriteFile(outPath, b, 0o644); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	if upd {
		latest := map[string]string{"version": ver}
		bl, _ := json.MarshalIndent(latest, "", "  ")
		_ = os.WriteFile(filepath.Join(outDir, "latest.json"), bl, 0o644)
	}
	// return manifest JSON
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// ===== ZIP filename decoding helpers =====
// Many ZIP creators on Windows do not set the UTF-8 flag and store filenames
// using a legacy codepage (CP866 or Windows-1251 for Russian). The stdlib
// falls back to CP437 in such cases which leads to mojibake for Cyrillic.
// We try to recover the correct filename by:
// 1) Preferring the Info-ZIP Unicode Path extra field (0x7075) if present.
// 2) If NonUTF8 is set and no Unicode Path is present, heuristically
//    re-interpret the CP437 string as bytes and decode using CP866/Win1251.
// 3) If the result looks like valid UTF-8 and improves Cyrillic ratio, use it.

// parseZipUnicodePath returns UTF-8 filename from the Info-ZIP Unicode Path extra field (0x7075) if present.
func parseZipUnicodePath(extra []byte) string {
	// Extra fields: [2 bytes header id][2 bytes data size][data] ...
	// 0x7075 ("up") layout: version(1), nameCRC32(4), utf8Name(rest)
	for i := 0; i+4 <= len(extra); {
		id := binary.LittleEndian.Uint16(extra[i:])
		sz := int(binary.LittleEndian.Uint16(extra[i+2:]))
		i += 4
		if i+sz > len(extra) {
			break
		}
		if id == 0x7075 && sz >= 5 {
			data := extra[i : i+sz]
			name := string(data[5:])
			if utf8.ValidString(name) && strings.TrimSpace(name) != "" {
				return name
			}
		}
		i += sz
	}
	return ""
}

func countCyrillicRunes(s string) (cyr, total int) {
	for _, r := range s {
		total++
		if (r >= '\u0400' && r <= '\u04FF') || (r >= '\u0500' && r <= '\u052F') {
			cyr++
		}
	}
	return
}

// tryFixCyrillicFromCP437 attempts to re-decode a mojibake filename that was decoded as CP437
// by encoding it back to CP437 bytes and then decoding with CP866 or Windows-1251.
func tryFixCyrillicFromCP437(name string) string {
	// Encode current runes to CP437 bytes
	b, err := charmap.CodePage437.NewEncoder().Bytes([]byte(name))
	if err != nil {
		return name
	}
	// Try CP866
	s866, err866 := charmap.CodePage866.NewDecoder().String(string(b))
	// Try Windows-1251
	s1251, err1251 := charmap.Windows1251.NewDecoder().String(string(b))

	best := name
	bestScore := -1
	// baseline score
	if utf8.ValidString(name) {
		c, t := countCyrillicRunes(name)
		if t > 0 {
			bestScore = c * 2 // prefer Cyrillic heavy
		} else {
			bestScore = 0
		}
	}
	if err866 == nil && utf8.ValidString(s866) {
		c, _ := countCyrillicRunes(s866)
		score := c * 2
		if score > bestScore {
			bestScore = score
			best = s866
		}
	}
	if err1251 == nil && utf8.ValidString(s1251) {
		c, _ := countCyrillicRunes(s1251)
		score := c * 2
		if score > bestScore {
			best = s1251
		}
	}
	return best
}

// zipFileDecodedName returns the best-effort UTF-8 filename for a zip.File
func zipFileDecodedName(f *zip.File) string {
	if n := parseZipUnicodePath(f.Extra); n != "" {
		return n
	}
	// If the UTF-8 flag is not set, the stdlib assumed CP437; try to fix Cyrillic
	if f.NonUTF8 {
		return tryFixCyrillicFromCP437(f.Name)
	}
	return f.Name
}

// unzipTo extracts a .zip archive into target directory, preserving structure
func unzipTo(zipPath, target string) error {
	r, err := zip.OpenReader(zipPath)
	if err != nil {
		return err
	}
	defer r.Close()
	for _, f := range r.File {
		// normalize entry name and guard against ZipSlip
		name := zipFileDecodedName(f)
		rel := filepath.ToSlash(strings.TrimSpace(name))
		// remove any drive letters or leading slashes/backslashes
		rel = strings.TrimLeft(rel, "/\\")
		// collapse any .. segments
		rel = filepath.ToSlash(filepath.Clean(rel))
		if rel == "." || rel == "" {
			continue
		}
		// ensure final destination is within target
		full := filepath.Join(target, rel)
		if !ensureWithin(target, full) {
			return fmt.Errorf("zip entry outside target: %s", rel)
		}
		// directory entry: check header info and suffix
		if f.FileInfo().IsDir() || strings.HasSuffix(rel, "/") {
			if err := os.MkdirAll(full, 0o755); err != nil {
				// Fallbacks: ensure parent exists, handle possible file-vs-dir collision
				_ = os.MkdirAll(filepath.Dir(full), 0o755)
				if err2 := os.MkdirAll(full, 0o755); err2 != nil {
					// if a file exists at 'full', remove it and retry
					if st, e := os.Stat(full); e == nil && !st.IsDir() {
						_ = os.Remove(full)
						if err3 := os.MkdirAll(full, 0o755); err3 == nil {
							continue
						}
					}
					return fmt.Errorf("mkdir dir failed for entry %q -> %s: %w", rel, full, err2)
				}
			}
			continue
		}
		// ensure directory exists
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			// handle possible file-vs-dir collision on parent
			parent := filepath.Dir(full)
			if st, e := os.Stat(parent); e == nil && !st.IsDir() {
				_ = os.Remove(parent)
				if err2 := os.MkdirAll(parent, 0o755); err2 != nil {
					return fmt.Errorf("mkdir parent failed for entry %q -> %s: %w", rel, parent, err)
				}
			} else {
				return fmt.Errorf("mkdir parent failed for entry %q -> %s: %w", rel, parent, err)
			}
		}
		rc, err := f.Open()
		if err != nil {
			return fmt.Errorf("open zip entry %q: %w", rel, err)
		}
		out, err := os.OpenFile(full, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o644)
		if err != nil {
			rc.Close()
			return fmt.Errorf("create file failed for entry %q -> %s: %w", rel, full, err)
		}
		if _, err := io.Copy(out, rc); err != nil {
			out.Close()
			rc.Close()
			return fmt.Errorf("write file failed for entry %q -> %s: %w", rel, full, err)
		}
		out.Close()
		rc.Close()
	}
	return nil
}

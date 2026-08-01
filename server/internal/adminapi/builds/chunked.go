package builds

// Chunked upload implementation for large ZIP files (launcher and games).
// Routes (registered by cmd/admin):
//   - POST /admin/api/upload/init     (JSON in/out)
//   - PUT  /admin/api/upload/chunk    (query: uploadId, index)
//   - GET  /admin/api/upload/status   (query: uploadId)
//   - POST /admin/api/upload/complete (query: uploadId) -> validates sha256 if provided, renames .part to .zip
//   - GET  /admin/api/upload/process  (query: uploadId) -> NDJSON stream: unzip + compose manifest
//   - POST /admin/api/upload/cleanup  -> removes stale/broken tmp uploads

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"time"

	"ChillHub/server/internal/adminutil"
)

const (
	uploadChunkSizeDefault = 8 << 20 // 8 MiB
	uploadExpire           = 12 * time.Hour
)

var uploadMu sync.Mutex

// uploadMeta is the resumable-upload state persisted next to the part file.
type uploadMeta struct {
	UploadID       string `json:"uploadId"`
	Kind           string `json:"kind"`
	GameID         string `json:"gameId"`
	Version        string `json:"version"`
	ZipName        string `json:"zipName"`
	TotalSize      int64  `json:"totalSize"`
	ChunkSize      int    `json:"chunkSize"`
	TotalChunks    int    `json:"totalChunks"`
	Received       []bool `json:"received"`
	ExpectedSha256 string `json:"expectedSha256"`
	UpdatedAt      int64  `json:"updatedAt"` // unix seconds
	Status         string `json:"status"`    // init|uploading|ready|processing|done|error
}

// logging helper with level and request-id (uploadId)
func linfo(id string, format string, a ...any) {
	if id == "" {
		id = "-"
	}
	log.Printf("[INFO] uploadId=%s "+format, append([]any{id}, a...)...)
}

func (h *Handlers) uploadBaseDir() string {
	return filepath.Join(h.root, "tmp", "uploads")
}

func (h *Handlers) uploadDir(id string) string { return filepath.Join(h.uploadBaseDir(), id) }
func (h *Handlers) uploadMetaPath(id string) string {
	return filepath.Join(h.uploadDir(id), "meta.json")
}
func (h *Handlers) uploadZipPartPath(id string) string {
	return filepath.Join(h.uploadDir(id), "upload.zip.part")
}
func (h *Handlers) uploadZipPath(id string) string {
	return filepath.Join(h.uploadDir(id), "upload.zip")
}

func (h *Handlers) readUploadMeta(id string) (*uploadMeta, error) {
	b, err := os.ReadFile(h.uploadMetaPath(id))
	if err != nil {
		return nil, err
	}
	var m uploadMeta
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, err
	}
	return &m, nil
}

func (h *Handlers) writeUploadMeta(m *uploadMeta) error {
	if err := os.MkdirAll(h.uploadDir(m.UploadID), 0o755); err != nil {
		return err
	}
	m.UpdatedAt = time.Now().Unix()
	b, _ := json.MarshalIndent(m, "", "  ")
	// Atomic write: write to a temp file and rename over meta.json
	dir := h.uploadDir(m.UploadID)
	tmp, err := os.CreateTemp(dir, "meta-*.json.tmp")
	if err != nil {
		return err
	}
	tmpPath := tmp.Name()
	if _, err := tmp.Write(b); err != nil {
		tmp.Close()
		_ = os.Remove(tmpPath)
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		_ = os.Remove(tmpPath)
		return err
	}
	if err := tmp.Close(); err != nil {
		_ = os.Remove(tmpPath)
		return err
	}
	return os.Rename(tmpPath, h.uploadMetaPath(m.UploadID))
}

// UploadCleanup triggers immediate cleanup of stale/broken tmp uploads.
func (h *Handlers) UploadCleanup(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !h.authorized(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	// Force-remove all entries under contentRoot/tmp
	tmpRoot := filepath.Join(h.root, "tmp")
	entries, err := os.ReadDir(tmpRoot)
	if err != nil {
		// If tmp root doesn't exist, nothing to remove
		adminutil.WriteJSON(w, map[string]any{"status": "ok", "removed": 0})
		return
	}
	removed := 0
	for _, e := range entries {
		p := filepath.Join(tmpRoot, e.Name())
		if err := os.RemoveAll(p); err != nil {
			log.Printf("[upload:cleanup] failed to remove %s: %v", p, err)
		} else {
			removed++
		}
	}
	adminutil.WriteJSON(w, map[string]any{"status": "ok", "removed": removed})
}

// UploadInit allocates an upload id and preallocates the part file.
func (h *Handlers) UploadInit(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	// Auth inside Go (nginx bypasses auth_request for these endpoints)
	if !h.authorized(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	var in struct {
		Kind           string `json:"kind"`
		GameID         string `json:"gameId"`
		Version        string `json:"version"`
		ZipName        string `json:"zipName"`
		TotalSize      int64  `json:"totalSize"`
		ChunkSize      int    `json:"chunkSize"`
		ExpectedSha256 string `json:"expectedSha256"`
	}
	dec := json.NewDecoder(r.Body)
	if err := dec.Decode(&in); err != nil {
		log.Printf("[upload:init] bad json: %v", err)
		http.Error(w, "bad json", http.StatusBadRequest)
		return
	}
	if strings.ToLower(strings.TrimSpace(in.Kind)) == "launcher" {
		in.GameID = "launcher"
	}
	if !adminutil.IsSafeGameID(in.GameID) || !adminutil.IsSafeVersion(in.Version) {
		log.Printf("[upload:init] invalid ids: kind=%s gameId=%q version=%q", in.Kind, in.GameID, in.Version)
		http.Error(w, "invalid gameId or version", http.StatusBadRequest)
		return
	}
	if in.TotalSize <= 0 {
		log.Printf("[upload:init] invalid totalSize: %d (gameId=%s version=%s)", in.TotalSize, in.GameID, in.Version)
		http.Error(w, "invalid totalSize", http.StatusBadRequest)
		return
	}
	// Heuristics and env-driven bounds
	envInt := func(name string, def int) int {
		if v := strings.TrimSpace(os.Getenv(name)); v != "" {
			if n, err := strconv.Atoi(v); err == nil && n > 0 {
				return n
			}
		}
		return def
	}
	minChunk := envInt("UPLOAD_CHUNK_MIN", 64<<10)  // 64 KiB
	maxChunk := envInt("UPLOAD_CHUNK_MAX", 512<<20) // 512 MiB
	maxParLimit := envInt("UPLOAD_MAX_PARALLEL", 100)
	if minChunk < (64 << 10) {
		minChunk = 64 << 10
	}
	if maxChunk < minChunk {
		maxChunk = minChunk
	}

	// Recommended chunk by total size buckets (clamped)
	recChunk := uploadChunkSizeDefault
	switch {
	case in.TotalSize <= 512<<20: // < 512 MiB
		recChunk = 4 << 20
	case in.TotalSize <= 2<<30: // < 2 GiB
		recChunk = 8 << 20
	case in.TotalSize <= 8<<30: // < 8 GiB
		recChunk = 16 << 20
	default:
		recChunk = 32 << 20
	}
	if recChunk < minChunk {
		recChunk = minChunk
	}
	if recChunk > maxChunk {
		recChunk = maxChunk
	}

	// If client didn't specify, use our recommendation; otherwise clamp client value
	if in.ChunkSize <= 0 {
		in.ChunkSize = recChunk
	}
	if in.ChunkSize < minChunk {
		in.ChunkSize = minChunk
	}
	if in.ChunkSize > maxChunk {
		in.ChunkSize = maxChunk
	}
	// free space precheck
	tmpRoot := h.uploadBaseDir()
	if err := os.MkdirAll(tmpRoot, 0o755); err != nil {
		log.Printf("[upload:init] mkdir tmpRoot=%s error: %v", tmpRoot, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	if free, err := freeSpaceBytes(tmpRoot); err == nil && free > 0 && uint64(in.TotalSize) > free {
		log.Printf("[upload:init] insufficient temp space: need=%d have=%d path=%s", in.TotalSize, free, tmpRoot)
		http.Error(w, fmt.Sprintf("insufficient temp space: need %d have %d", in.TotalSize, free), http.StatusInsufficientStorage)
		return
	}
	// allocate uploadId
	id := adminutil.NewBuildID()
	m := &uploadMeta{
		UploadID: id, Kind: strings.ToLower(in.Kind), GameID: in.GameID, Version: in.Version,
		ZipName: in.ZipName, TotalSize: in.TotalSize, ChunkSize: in.ChunkSize,
		TotalChunks:    int((in.TotalSize + int64(in.ChunkSize) - 1) / int64(in.ChunkSize)),
		Received:       make([]bool, int((in.TotalSize+int64(in.ChunkSize)-1)/int64(in.ChunkSize))),
		ExpectedSha256: strings.ToLower(strings.TrimSpace(in.ExpectedSha256)),
		Status:         "init",
	}
	// create per-upload directory and part file (truncate to size)
	if err := os.MkdirAll(h.uploadDir(id), 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	part := h.uploadZipPartPath(id)
	f, err := os.OpenFile(part, os.O_CREATE|os.O_RDWR, 0o644)
	if err != nil {
		log.Printf("[upload:init] open part uploadId=%s path=%s error: %v", id, part, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	if err := f.Truncate(in.TotalSize); err != nil {
		f.Close()
		log.Printf("[upload:init] truncate part uploadId=%s size=%d error: %v", id, in.TotalSize, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	f.Close()
	if err := h.writeUploadMeta(m); err != nil {
		log.Printf("[upload:init] write meta uploadId=%s error: %v", id, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	// Recommend maxParallel based on CPUs (2..maxParLimit)
	cpus := runtime.NumCPU()
	recPar := cpus
	if recPar < 2 {
		recPar = 2
	}
	if recPar > maxParLimit {
		recPar = maxParLimit
	}
	w.Header().Set("X-Request-ID", id)
	linfo(id, "init ok kind=%s gameId=%s version=%s zip=%s total=%d chunkSize=%d recChunk=%d maxPar=%d from=%s",
		strings.ToLower(in.Kind), in.GameID, in.Version, in.ZipName, in.TotalSize, m.ChunkSize, recChunk, recPar, r.RemoteAddr)
	adminutil.WriteJSON(w, map[string]any{
		"uploadId":             id,
		"chunkSize":            m.ChunkSize,
		"totalChunks":          m.TotalChunks,
		"maxParallel":          recPar,
		"recommendedChunkSize": recChunk,
	})
}

// UploadChunk writes one chunk at its offset in the part file.
func (h *Handlers) UploadChunk(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPut && r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !h.authorized(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("uploadId"))
	idxStr := strings.TrimSpace(r.URL.Query().Get("index"))
	if id == "" || idxStr == "" {
		http.Error(w, "missing id/index", http.StatusBadRequest)
		return
	}
	w.Header().Set("X-Request-ID", id)
	idx, err := strconv.Atoi(idxStr)
	if err != nil || idx < 0 {
		http.Error(w, "bad index", http.StatusBadRequest)
		return
	}
	var m *uploadMeta
	for attempt := 0; attempt < 5; attempt++ {
		if m, err = h.readUploadMeta(id); err == nil {
			break
		}
		time.Sleep(time.Duration(5*(attempt+1)) * time.Millisecond) // 5,10,15,20,25ms
	}
	if err != nil {
		log.Printf("[upload:chunk] meta not found uploadId=%s index=%s remote=%s error=%v", id, idxStr, r.RemoteAddr, err)
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	if idx >= m.TotalChunks {
		http.Error(w, "index out of range", http.StatusBadRequest)
		return
	}
	// compute expected size
	exp := m.ChunkSize
	if idx == m.TotalChunks-1 {
		rem := int(m.TotalSize - int64((m.TotalChunks-1)*m.ChunkSize))
		if rem > 0 {
			exp = rem
		}
	}
	// write at offset
	off := int64(idx * m.ChunkSize)
	f, err := os.OpenFile(h.uploadZipPartPath(id), os.O_WRONLY, 0)
	if err != nil {
		log.Printf("[upload:chunk] open part uploadId=%s error: %v", id, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	t0 := time.Now()
	n, werr := io.CopyN(&writeAt{f: f, off: off}, r.Body, int64(exp))
	f.Close()
	if werr != nil && !errors.Is(werr, io.EOF) {
		log.Printf("[upload:chunk] write uploadId=%s index=%d error: %v", id, idx, werr)
		http.Error(w, werr.Error(), http.StatusInternalServerError)
		return
	}
	if int(n) != exp {
		log.Printf("[upload:chunk] short chunk uploadId=%s index=%d got=%d want=%d", id, idx, n, exp)
		http.Error(w, fmt.Sprintf("short chunk: got %d want %d", n, exp), http.StatusBadRequest)
		return
	}
	// mark received
	uploadMu.Lock()
	// Reload latest meta under the lock to avoid lost updates from concurrent chunk handlers
	if mLatest, err2 := h.readUploadMeta(id); err2 == nil && mLatest != nil {
		m = mLatest
	}
	if idx >= 0 && idx < len(m.Received) {
		m.Received[idx] = true
	}
	m.Status = "uploading"
	_ = h.writeUploadMeta(m)
	uploadMu.Unlock()
	adminutil.WriteJSON(w, map[string]any{"status": "ok", "bytes": int(n), "writeMs": time.Since(t0).Milliseconds()})
}

type writeAt struct {
	f   *os.File
	off int64
}

func (w *writeAt) Write(p []byte) (int, error) {
	if _, err := w.f.Seek(w.off, io.SeekStart); err != nil {
		return 0, err
	}
	n, err := w.f.Write(p)
	w.off += int64(n)
	return n, err
}

// UploadStatus reports which chunks have been received.
func (h *Handlers) UploadStatus(w http.ResponseWriter, r *http.Request) {
	if !h.authorized(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("uploadId"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	w.Header().Set("X-Request-ID", id)
	m, err := h.readUploadMeta(id)
	if err != nil {
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	bits := make([]int, 0, m.TotalChunks)
	for i, ok := range m.Received {
		if ok {
			bits = append(bits, i)
		}
	}
	adminutil.WriteJSON(w, map[string]any{
		"uploadId":    m.UploadID,
		"received":    bits,
		"totalChunks": m.TotalChunks,
		"chunkSize":   m.ChunkSize,
	})
}

// UploadComplete verifies the assembled part file and renames it to upload.zip.
func (h *Handlers) UploadComplete(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if !h.authorized(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("uploadId"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	m, err := h.readUploadMeta(id)
	if err != nil {
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	// ensure all chunks present
	for i, ok := range m.Received {
		if !ok {
			log.Printf("[upload:complete] missing chunk uploadId=%s index=%d total=%d", id, i, len(m.Received))
			http.Error(w, fmt.Sprintf("missing chunk %d", i), http.StatusBadRequest)
			return
		}
	}
	// compute sha256 of full file
	f, err := os.Open(h.uploadZipPartPath(id))
	if err != nil {
		log.Printf("[upload:complete] open part uploadId=%s error: %v", id, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	hash := sha256.New()
	if _, err := io.Copy(hash, f); err != nil {
		f.Close()
		log.Printf("[upload:complete] read part uploadId=%s error: %v", id, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	f.Close()
	sum := strings.ToLower(hex.EncodeToString(hash.Sum(nil)))
	if m.ExpectedSha256 != "" && sum != m.ExpectedSha256 {
		log.Printf("[upload:complete] sha256 mismatch uploadId=%s expected=%s actual=%s", id, m.ExpectedSha256, sum)
		m.Status = "error"
		_ = h.writeUploadMeta(m)
		http.Error(w, "sha256 mismatch", http.StatusBadRequest)
		return
	}
	// rename to final zip inside upload dir
	if err := os.Rename(h.uploadZipPartPath(id), h.uploadZipPath(id)); err != nil {
		log.Printf("[upload:complete] rename part->zip uploadId=%s error: %v", id, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	m.Status = "ready"
	_ = h.writeUploadMeta(m)
	adminutil.WriteJSON(w, map[string]any{"status": "ok", "sha256": sum})
}

// UploadProcessStream extracts the completed upload and streams NDJSON:
// start, unzip entries, compose files, done.
func (h *Handlers) UploadProcessStream(w http.ResponseWriter, r *http.Request) {
	if !h.authorized(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("uploadId"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	m, err := h.readUploadMeta(id)
	if err != nil {
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	w.Header().Set("Content-Type", "application/x-ndjson")
	fl := adminutil.FlusherFor(w)
	fmt.Fprintf(w, "{\"type\":\"start\",\"kind\":%q,\"gameId\":%q,\"version\":%q}\n", m.Kind, m.GameID, m.Version)
	fl.Flush()
	zipPath := h.uploadZipPath(id)
	// The gameId/version pair was validated at upload init, but re-check before
	// it is turned into a filesystem path again.
	if !adminutil.IsSafeGameID(m.GameID) || !adminutil.IsSafeVersion(m.Version) {
		http.Error(w, "invalid gameId or version", http.StatusBadRequest)
		return
	}
	// Extract into a staging dir on the same volume and publish with a single
	// rename, so an interrupted run never leaves a partial version in place.
	finalVerDir := filepath.Join(h.root, "content", m.GameID, m.Version)
	stageDir, filesRoot, err := h.stageVersionDir(m.GameID, m.Version)
	if err != nil {
		streamError(w, fl, err.Error())
		return
	}
	promoted := false
	defer func() {
		if !promoted {
			_ = os.RemoveAll(stageDir)
		}
	}()
	// estimate and free-space precheck (optional)
	if needBytes, err := estimateZipUncompressedSize(zipPath); err == nil {
		if freeBytes, ferr := freeSpaceBytes(filesRoot); ferr == nil && freeBytes > 0 && needBytes > freeBytes {
			http.Error(w, fmt.Sprintf("insufficient disk space: need %d bytes, have %d bytes", needBytes, freeBytes), http.StatusInsufficientStorage)
			return
		}
	}
	if !streamUnzip(w, fl, zipPath, filesRoot) {
		return
	}
	files, emptyDirs, ok := streamCompose(w, fl, filesRoot)
	if !ok {
		return
	}
	mOut := manifest{
		Version:   m.Version,
		BuildID:   adminutil.NewBuildID(),
		GameID:    m.GameID,
		CreatedAt: time.Now().UTC().Format(time.RFC3339),
		Files:     files,
		EmptyDirs: emptyDirs,
	}
	// Everything is extracted and hashed: publish the build in one rename.
	if err := promoteVersionDir(stageDir, finalVerDir); err != nil {
		streamError(w, fl, "activate failed: "+err.Error())
		return
	}
	promoted = true
	// update latest.json is opted-in by client via existing API; here keep minimal
	outPath, _, err := h.writeManifest(mOut, false)
	if err != nil {
		streamError(w, fl, err.Error())
		return
	}
	fmt.Fprintf(w, "{\"type\":\"done\",\"outPath\":%q}\n", outPath)
	fl.Flush()
}

// StartUploadJanitor removes expired upload staging directories. It blocks and
// is meant to run in its own goroutine.
func (h *Handlers) StartUploadJanitor() {
	for {
		time.Sleep(15 * time.Minute)
		d := h.uploadBaseDir()
		entries, err := os.ReadDir(d)
		if err != nil {
			continue
		}
		cut := time.Now().Add(-uploadExpire).Unix()
		for _, e := range entries {
			if !e.IsDir() {
				continue
			}
			id := e.Name()
			m, err := h.readUploadMeta(id)
			if err != nil { // no meta -> stale dir
				_ = os.RemoveAll(h.uploadDir(id))
				continue
			}
			if m.Status == "done" || m.Status == "processed" {
				continue
			}
			if m.UpdatedAt == 0 || m.UpdatedAt < cut {
				_ = os.RemoveAll(h.uploadDir(id))
			}
		}
	}
}

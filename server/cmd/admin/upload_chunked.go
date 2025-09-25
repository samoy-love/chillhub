package main

import (
	"archive/zip"
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
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"

	"runtime"

	"github.com/zeebo/blake3"
)

// Chunked upload implementation for large ZIP files (launcher and games)
// Endpoints registered in init():
//  - POST   /admin/api/upload/init     (JSON in/out)
//  - GET    /admin/api/upload/status   (query: uploadId)
//  - POST   /admin/api/upload/complete (query: uploadId) -> validates sha256 if provided, renames .part to .zip
//  - GET    /admin/api/upload/process  (query: uploadId) -> NDJSON stream: unzip + compose manifest

const (
	uploadChunkSizeDefault = 8 << 20 // 8 MiB
	uploadMaxParallel      = 100
	uploadExpire           = 12 * time.Hour
)

var uploadMu sync.Mutex

// Run the janitor once immediately (same logic as periodic, without sleep loop)
func runUploadJanitorOnce() int {
	d := uploadBaseDir()
	entries, err := os.ReadDir(d)
	if err != nil {
		return 0
	}
	cut := time.Now().Add(-uploadExpire).Unix()
	removed := 0
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		id := e.Name()
		m, err := readUploadMeta(id)
		if err != nil { // no meta -> stale dir
			_ = os.RemoveAll(uploadDir(id))
			removed++
			continue
		}
		if m.Status == "done" || m.Status == "processed" {
			continue
		}
		if m.UpdatedAt == 0 || m.UpdatedAt < cut {
			_ = os.RemoveAll(uploadDir(id))
			removed++
		}
	}
	return removed
}

// POST /admin/api/upload/cleanup — trigger immediate cleanup of stale/broken tmp uploads
func handleUploadCleanup(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if _, user := currentUser(r); user == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	n := runUploadJanitorOnce()
	writeJSON(w, map[string]any{"status": "ok", "removed": n})
}

// logging helpers with levels and request-id (uploadId)
func linfo(id string, format string, a ...any) {
	if id == "" {
		id = "-"
	}
	log.Printf("[INFO] uploadId=%s "+format, append([]any{id}, a...)...)
}
func lwarn(id string, format string, a ...any) {
	if id == "" {
		id = "-"
	}
	log.Printf("[WARN] uploadId=%s "+format, append([]any{id}, a...)...)
}
func lerr(id string, format string, a ...any) {
	if id == "" {
		id = "-"
	}
	log.Printf("[ERROR] uploadId=%s "+format, append([]any{id}, a...)...)
}

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

func uploadBaseDir() string {
	return filepath.Join(contentRoot, "tmp", "uploads")
}

func uploadDir(id string) string         { return filepath.Join(uploadBaseDir(), id) }
func uploadMetaPath(id string) string    { return filepath.Join(uploadDir(id), "meta.json") }
func uploadZipPartPath(id string) string { return filepath.Join(uploadDir(id), "upload.zip.part") }
func uploadZipPath(id string) string     { return filepath.Join(uploadDir(id), "upload.zip") }

func readUploadMeta(id string) (*uploadMeta, error) {
	b, err := os.ReadFile(uploadMetaPath(id))
	if err != nil {
		return nil, err
	}
	var m uploadMeta
	if err := json.Unmarshal(b, &m); err != nil {
		return nil, err
	}
	return &m, nil
}

func writeUploadMeta(m *uploadMeta) error {
	if err := os.MkdirAll(uploadDir(m.UploadID), 0o755); err != nil {
		return err
	}
	m.UpdatedAt = time.Now().Unix()
	b, _ := json.MarshalIndent(m, "", "  ")
	return os.WriteFile(uploadMetaPath(m.UploadID), b, 0o644)
}

func handleUploadInit(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	// Auth inside Go (nginx bypasses auth_request for these endpoints)
	if _, user := currentUser(r); user == "" {
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
	if !isSafeGameID(in.GameID) || !isSafeVersion(in.Version) {
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
	minChunk := envInt("UPLOAD_CHUNK_MIN", 64<<10)   // 64 KiB
	maxChunk := envInt("UPLOAD_CHUNK_MAX", 512<<20)  // 512 MiB
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
	tmpRoot := uploadBaseDir()
	if err := os.MkdirAll(tmpRoot, 0o755); err != nil {
		log.Printf("[upload:init] mkdir tmpRoot=%s error: %v", tmpRoot, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	if free, err := getFreeSpaceBytes(tmpRoot); err == nil && free > 0 && uint64(in.TotalSize) > free {
		log.Printf("[upload:init] insufficient temp space: need=%d have=%d path=%s", in.TotalSize, free, tmpRoot)
		http.Error(w, fmt.Sprintf("insufficient temp space: need %d have %d", in.TotalSize, free), 507)
		return
	}
	// allocate uploadId
	id := newBuildID()
	m := &uploadMeta{
		UploadID: id, Kind: strings.ToLower(in.Kind), GameID: in.GameID, Version: in.Version,
		ZipName: in.ZipName, TotalSize: in.TotalSize, ChunkSize: in.ChunkSize,
		TotalChunks:    int((in.TotalSize + int64(in.ChunkSize) - 1) / int64(in.ChunkSize)),
		Received:       make([]bool, int((in.TotalSize+int64(in.ChunkSize)-1)/int64(in.ChunkSize))),
		ExpectedSha256: strings.ToLower(strings.TrimSpace(in.ExpectedSha256)),
		Status:         "init",
	}
	// create per-upload directory and part file (truncate to size)
	if err := os.MkdirAll(uploadDir(id), 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	part := uploadZipPartPath(id)
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
	if err := writeUploadMeta(m); err != nil {
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
	writeJSON(w, map[string]any{
		"uploadId":             id,
		"chunkSize":            m.ChunkSize,
		"totalChunks":          m.TotalChunks,
		"maxParallel":          recPar,
		"recommendedChunkSize": recChunk,
	})
}

func handleUploadChunk(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPut && r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if _, user := currentUser(r); user == "" {
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
	m, err := readUploadMeta(id)
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
	f, err := os.OpenFile(uploadZipPartPath(id), os.O_WRONLY, 0)
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
	if mLatest, err2 := readUploadMeta(id); err2 == nil && mLatest != nil {
		m = mLatest
	}
	if idx >= 0 && idx < len(m.Received) {
		m.Received[idx] = true
	}
	m.Status = "uploading"
	_ = writeUploadMeta(m)
	uploadMu.Unlock()
	writeJSON(w, map[string]any{"status": "ok", "bytes": int(n), "writeMs": time.Since(t0).Milliseconds()})
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

func handleUploadStatus(w http.ResponseWriter, r *http.Request) {
	if _, user := currentUser(r); user == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("uploadId"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	w.Header().Set("X-Request-ID", id)
	m, err := readUploadMeta(id)
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
	writeJSON(w, map[string]any{
		"uploadId":    m.UploadID,
		"received":    bits,
		"totalChunks": m.TotalChunks,
		"chunkSize":   m.ChunkSize,
	})
}

func handleUploadComplete(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if _, user := currentUser(r); user == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("uploadId"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	m, err := readUploadMeta(id)
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
	f, err := os.Open(uploadZipPartPath(id))
	if err != nil {
		log.Printf("[upload:complete] open part uploadId=%s error: %v", id, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		f.Close()
		log.Printf("[upload:complete] read part uploadId=%s error: %v", id, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	f.Close()
	sum := strings.ToLower(hex.EncodeToString(h.Sum(nil)))
	if m.ExpectedSha256 != "" && sum != m.ExpectedSha256 {
		log.Printf("[upload:complete] sha256 mismatch uploadId=%s expected=%s actual=%s", id, m.ExpectedSha256, sum)
		m.Status = "error"
		_ = writeUploadMeta(m)
		http.Error(w, "sha256 mismatch", http.StatusBadRequest)
		return
	}
	// rename to final zip inside upload dir
	if err := os.Rename(uploadZipPartPath(id), uploadZipPath(id)); err != nil {
		log.Printf("[upload:complete] rename part->zip uploadId=%s error: %v", id, err)
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	m.Status = "ready"
	_ = writeUploadMeta(m)
	writeJSON(w, map[string]any{"status": "ok", "sha256": sum})
}

// Streams NDJSON: start, unzip entries, compose files, done (reuses helpers from main.go)
func handleUploadProcessStream(w http.ResponseWriter, r *http.Request) {
	if _, user := currentUser(r); user == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	id := strings.TrimSpace(r.URL.Query().Get("uploadId"))
	if id == "" {
		http.Error(w, "missing id", http.StatusBadRequest)
		return
	}
	m, err := readUploadMeta(id)
	if err != nil {
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	w.Header().Set("Content-Type", "application/x-ndjson")
	type flusher interface{ Flush() }
	var fl flusher
	if f, ok := w.(http.Flusher); ok {
		fl = f
	} else {
		fl = flusher(noopFlusher{})
	}
	fmt.Fprintf(w, "{\"type\":\"start\",\"kind\":%q,\"gameId\":%q,\"version\":%q}\n", m.Kind, m.GameID, m.Version)
	fl.Flush()
	zipPath := uploadZipPath(id)
	// files root
	filesRoot := filepath.Join(contentRoot, "content", m.GameID, m.Version, "files")
	if err := os.MkdirAll(filesRoot, 0o755); err != nil {
		fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
		fl.Flush()
		return
	}
	// estimate and free-space precheck (optional)
	if needBytes, err := estimateZipUncompressedSize(zipPath); err == nil {
		if freeBytes, ferr := getFreeSpaceBytes(filesRoot); ferr == nil && freeBytes > 0 && needBytes > freeBytes {
			http.Error(w, fmt.Sprintf("insufficient disk space: need %d bytes, have %d bytes", needBytes, freeBytes), 507)
			return
		}
	}
	// unzip with progress (copy from handleUploadStream)
	zr, err := zip.OpenReader(zipPath)
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
	// build manifest (reuse from handleUploadStream)
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
		files = append(files, manifestFile{Path: rel, Size: info.Size(), Blake3: hex.EncodeToString(hB3.Sum(nil)), Sha256: hex.EncodeToString(hSha.Sum(nil)), Executable: isExecutable(rel)})
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
	mOut := manifest{Version: m.Version, BuildID: newBuildID(), GameID: m.GameID, CreatedAt: time.Now().UTC().Format(time.RFC3339), Files: files, EmptyDirs: emptyDirs, Signature: "dev-mock-signature"}
	outDir := filepath.Join(contentRoot, "manifests", m.GameID)
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
		fl.Flush()
		return
	}
	outPath := filepath.Join(outDir, m.Version+".json")
	b, _ := json.MarshalIndent(mOut, "", "  ")
	if err := os.WriteFile(outPath, b, 0o644); err != nil {
		fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", err.Error())
		fl.Flush()
		return
	}
	// update latest.json is opted-in by client via existing API; here keep minimal
	fmt.Fprintf(w, "{\"type\":\"done\",\"outPath\":%q}\n", outPath)
	fl.Flush()
}

func startUploadJanitor() {
	for {
		time.Sleep(15 * time.Minute)
		d := uploadBaseDir()
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
			m, err := readUploadMeta(id)
			if err != nil { // no meta -> stale dir
				_ = os.RemoveAll(uploadDir(id))
				continue
			}
			if m.Status == "done" || m.Status == "processed" {
				continue
			}
			if m.UpdatedAt == 0 || m.UpdatedAt < cut {
				_ = os.RemoveAll(uploadDir(id))
			}
		}
	}
}

// Register HTTP routes for chunked upload and start janitor
func init() {
	http.HandleFunc("/admin/api/upload/init", handleUploadInit)
	http.HandleFunc("/admin/api/upload/chunk", handleUploadChunk)
	http.HandleFunc("/admin/api/upload/status", handleUploadStatus)
	http.HandleFunc("/admin/api/upload/complete", handleUploadComplete)
	http.HandleFunc("/admin/api/upload/process", handleUploadProcessStream)
	http.HandleFunc("/admin/api/upload/cleanup", handleUploadCleanup)
	go startUploadJanitor()
}

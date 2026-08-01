package builds

import (
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"ChillHub/server/internal/adminutil"
)

// Upload handles a plain multipart ZIP upload and publishes a release
// (launcher or game), returning the manifest JSON.
func (h *Handlers) Upload(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
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
	if !adminutil.IsSafeGameID(gid) {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return
	}
	if !adminutil.IsSafeVersion(ver) {
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
	filesRoot := filepath.Join(h.root, "content", gid, ver, "files")
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
	files, emptyDirs, err := scanManifest(filesRoot)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}

	m := manifest{
		Version:   ver,
		BuildID:   adminutil.NewBuildID(),
		GameID:    gid,
		CreatedAt: time.Now().UTC().Format(time.RFC3339),
		Files:     files,
		EmptyDirs: emptyDirs,
	}
	_, b, err := h.writeManifest(m, upd)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	// return manifest JSON
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// UploadStream uploads a ZIP and streams progress (NDJSON): start, unzip
// entries, compose files, done.
func (h *Handlers) UploadStream(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	// Enforce auth here (since nginx bypasses auth_request for this endpoint)
	if !h.authorized(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	// Streaming setup (tolerate environments without http.Flusher)
	w.Header().Set("Content-Type", "application/x-ndjson")
	fl := adminutil.FlusherFor(w)

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
		tmpDir := filepath.Join(h.root, "tmp")
		if err := os.MkdirAll(tmpDir, 0o755); err == nil {
			if free, ferr := freeSpaceBytes(tmpDir); ferr == nil && free > 0 && uint64(r.ContentLength) > free {
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
			tmpDir := filepath.Join(h.root, "tmp")
			if err := os.MkdirAll(tmpDir, 0o755); err != nil {
				streamError(w, fl, err.Error())
				part.Close()
				return
			}
			tmpZip, err := os.CreateTemp(tmpDir, "upload-*.zip")
			if err != nil {
				streamError(w, fl, err.Error())
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
				streamError(w, fl, cerr.Error())
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
	if !adminutil.IsSafeGameID(gid) || !adminutil.IsSafeVersion(ver) {
		http.Error(w, "invalid gameId or version", http.StatusBadRequest)
		return
	}
	if tmpName == "" {
		streamError(w, fl, "missing zip part")
		return
	}

	// Send start event after we know parameters
	fmt.Fprintf(w, "{\"type\":\"start\",\"kind\":%q,\"gameId\":%q,\"version\":%q}\n", kind, gid, ver)
	fl.Flush()

	// Save zip to temp was already done (tmpName). Extract into a staging dir on
	// the same volume; the published directory is only replaced once the whole
	// build is on disk, so an aborted upload can never leave a half version live.
	finalVerDir := filepath.Join(h.root, "content", gid, ver)
	stageDir, filesRoot, err := h.stageVersionDir(gid, ver)
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

	// Check free space before unzip (estimate total uncompressed size of ZIP)
	if needBytes, err := estimateZipUncompressedSize(tmpName); err == nil {
		if freeBytes, ferr := freeSpaceBytes(filesRoot); ferr == nil && freeBytes > 0 && needBytes > freeBytes {
			http.Error(w, fmt.Sprintf("insufficient disk space: need %d bytes, have %d bytes", needBytes, freeBytes), http.StatusInsufficientStorage)
			return
		}
	}

	// Unzip with progress
	if !streamUnzip(w, fl, tmpName, filesRoot) {
		return
	}
	// Remove temp zip
	os.Remove(tmpName)

	// Compose manifest with progress: pre-scan totals first
	totalFiles, totalBytes := countTree(filesRoot)
	fmt.Fprintf(w, "{\"type\":\"composeStart\",\"totalFiles\":%d,\"totalBytes\":%d}\n", totalFiles, totalBytes)
	fl.Flush()

	files, emptyDirs, ok := streamCompose(w, fl, filesRoot)
	if !ok {
		return
	}
	m := manifest{
		Version:   ver,
		BuildID:   adminutil.NewBuildID(),
		GameID:    gid,
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

	outPath, _, err := h.writeManifest(m, upd)
	if err != nil {
		streamError(w, fl, err.Error())
		return
	}

	fmt.Fprintf(w, "{\"type\":\"done\",\"outPath\":%q}\n", outPath)
	fl.Flush()
}

// countTree returns the number of files and their total size under root.
func countTree(root string) (int, int64) {
	var totalFiles int
	var totalBytes int64
	filepath.WalkDir(root, func(path string, d os.DirEntry, err error) error {
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
	return totalFiles, totalBytes
}

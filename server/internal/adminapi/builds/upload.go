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

// tmpDir is the upload scratch directory INSIDE the content root.
//
// It must not be the system temp directory: the ZIPs are up to 30 GB (nginx
// client_max_body_size), so spooling them to /tmp fills the root partition
// while the free-space precheck happily measures the — much larger — content
// volume. Keeping the scratch file next to the content also makes every
// subsequent move a same-volume rename.
func (h *Handlers) tmpDir() string { return filepath.Join(h.root, "tmp") }

// Upload handles a plain multipart ZIP upload and publishes a release
// (launcher or game), returning the manifest JSON.
func (h *Handlers) Upload(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	// The body is read part by part rather than through ParseMultipartForm:
	// ParseMultipartForm spools everything above its memory budget into
	// os.TempDir(), which for a 30 GB archive meant a second full copy on the
	// root partition on top of the one we write ourselves.
	var tmpName string
	defer func() {
		if tmpName != "" {
			_ = os.Remove(tmpName)
		}
	}()
	parts, code, err := readUploadParts(r, h.tmpDir(), nil)
	if parts != nil {
		tmpName = parts.tmpName
	}
	if err != nil {
		http.Error(w, err.Error(), code)
		return
	}
	kind := parts.kind
	gid := parts.gid
	ver := parts.ver
	upd := parts.updateLatest
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

	if tmpName == "" {
		http.Error(w, "missing zip part", http.StatusBadRequest)
		return
	}
	log.Printf("/admin/upload: kind=%s gid=%s ver=%s zip=%s bytes=%d", kind, gid, ver, parts.filename, parts.saved)

	// Extract into a staging directory next to the published one, exactly like
	// UploadStream and UploadProcessStream do. Writing straight into
	// content/<gid>/<ver>/files would leave a half-extracted, already published
	// version behind if the request is aborted mid-way.
	finalVerDir := filepath.Join(h.root, "content", gid, ver)
	stageDir, filesRoot, err := h.stageVersionDir(gid, ver)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	promoted := false
	defer func() {
		if !promoted {
			_ = os.RemoveAll(stageDir)
		}
	}()

	// Same precheck as the streaming paths: refuse an archive that cannot fit
	// rather than filling the volume and failing halfway through extraction.
	// filesRoot is under the content root, i.e. the same volume the ZIP was
	// just written to, so this now measures the volume that will actually be
	// filled.
	if needBytes, err := estimateZipUncompressedSize(tmpName); err == nil {
		if freeBytes, ferr := freeSpaceBytes(filesRoot); ferr == nil && freeBytes > 0 && needBytes > freeBytes {
			http.Error(w, fmt.Sprintf("insufficient disk space: need %d bytes, have %d bytes", needBytes, freeBytes), http.StatusInsufficientStorage)
			return
		}
	}

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

	// Everything is extracted and hashed: publish the build in one rename.
	if err := promoteVersionDir(stageDir, finalVerDir); err != nil {
		http.Error(w, "activate failed: "+err.Error(), http.StatusInternalServerError)
		return
	}
	promoted = true

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

// uploadParts is the decoded multipart body of a publish request: the small
// form fields, plus the ZIP already spooled to disk.
type uploadParts struct {
	kind         string
	gid          string
	ver          string
	updateLatest bool
	filename     string
	tmpName      string
	saved        int64
}

// maxUploadFieldBytes caps the small text fields of a publish request. They are
// identifiers a few dozen bytes long; the cap only stops a malformed body from
// being read into memory in full.
const maxUploadFieldBytes = 1 << 20

// readUploadParts streams a multipart publish request: text fields are read
// into memory under a cap, the "zip" part goes straight to a temp file in
// tmpDir. onZipSaved, when set, is called right after the archive has landed
// (the NDJSON handler uses it to emit its zipSaved event).
//
// It returns the HTTP status to answer with alongside the error. On failure the
// temp file is left in parts.tmpName for the caller's cleanup defer, which is
// already armed before this is called.
func readUploadParts(r *http.Request, tmpDir string, onZipSaved func(filename string, n int64)) (*uploadParts, int, error) {
	out := &uploadParts{}
	mr, err := r.MultipartReader()
	if err != nil {
		return out, http.StatusBadRequest, fmt.Errorf("multipart reader error: %w", err)
	}
	field := func(part io.Reader) string {
		b, _ := io.ReadAll(io.LimitReader(part, maxUploadFieldBytes))
		return strings.TrimSpace(string(b))
	}
	for {
		part, perr := mr.NextPart()
		if perr == io.EOF {
			break
		}
		if perr != nil {
			return out, http.StatusBadRequest, perr
		}
		switch strings.TrimSpace(part.FormName()) {
		case "":
			io.Copy(io.Discard, part)
		case "kind":
			out.kind = strings.ToLower(field(part))
		case "gameId":
			out.gid = field(part)
		case "version":
			out.ver = field(part)
		case "updateLatest":
			out.updateLatest = field(part) == "1"
		case "zip":
			if err := os.MkdirAll(tmpDir, 0o755); err != nil {
				part.Close()
				return out, http.StatusInternalServerError, err
			}
			tmpZip, err := os.CreateTemp(tmpDir, "upload-*.zip")
			if err != nil {
				part.Close()
				return out, http.StatusInternalServerError, err
			}
			out.tmpName = tmpZip.Name()
			out.filename = part.FileName()
			// A larger buffer pays off on multi-gigabyte uploads.
			buf := make([]byte, 4<<20) // 4 MiB
			n, cerr := io.CopyBuffer(tmpZip, part, buf)
			_ = tmpZip.Close()
			_ = part.Close()
			if cerr != nil {
				return out, http.StatusInternalServerError, cerr
			}
			out.saved = n
			if onZipSaved != nil {
				onZipSaved(out.filename, n)
			}
		default:
			// Consume but ignore other fields
			io.Copy(io.Discard, part)
		}
		_ = part.Close()
	}
	return out, http.StatusOK, nil
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
	// Streaming setup (tolerate environments without http.Flusher). Everything
	// this handler writes goes through nw, so it knows whether an error can
	// still be reported as an HTTP status or has to become an error event.
	w.Header().Set("Content-Type", "application/x-ndjson")
	nw := newNDJSONWriter(w)
	fl := adminutil.Flusher(nw)

	// The temp ZIP is this handler's alone: whatever happens below — a rejected
	// parameter, a full disk, a broken archive — it must not survive the request.
	// Only the successful path removes it early (and a second Remove is a no-op).
	var tmpName string
	defer func() {
		if tmpName != "" {
			_ = os.Remove(tmpName)
		}
	}()

	// Optional precheck: ensure enough temp space based on Content-Length if known
	if r.ContentLength > 0 {
		if err := os.MkdirAll(h.tmpDir(), 0o755); err == nil {
			if free, ferr := freeSpaceBytes(h.tmpDir()); ferr == nil && free > 0 && uint64(r.ContentLength) > free {
				nw.fail(http.StatusInsufficientStorage, fmt.Sprintf("insufficient temp space: need %d bytes, have %d bytes", r.ContentLength, free))
				return
			}
		}
	}

	parts, code, err := readUploadParts(r, h.tmpDir(), func(filename string, n int64) {
		fmt.Fprintf(nw, "{\"type\":\"zipSaved\",\"filename\":%q,\"bytes\":%d}\n", filename, n)
		fl.Flush()
	})
	if parts != nil {
		tmpName = parts.tmpName
	}
	if err != nil {
		nw.fail(code, err.Error())
		return
	}
	kind, gid, ver, upd := parts.kind, parts.gid, parts.ver, parts.updateLatest

	if kind == "launcher" {
		gid = "launcher"
	}
	// These checks run after the parts loop, i.e. after the zipSaved event has
	// already been flushed: nw.fail turns them into error events instead of an
	// http.Error whose status is ignored and whose plain-text body corrupts the
	// stream (leaving the client to conclude the build was published).
	if kind == "" {
		nw.fail(http.StatusBadRequest, "missing kind (launcher|game)")
		return
	}
	if ver == "" {
		nw.fail(http.StatusBadRequest, "missing version")
		return
	}
	if kind == "game" && strings.TrimSpace(gid) == "" {
		nw.fail(http.StatusBadRequest, "missing gameId for kind=game")
		return
	}
	if !adminutil.IsSafeGameID(gid) || !adminutil.IsSafeVersion(ver) {
		nw.fail(http.StatusBadRequest, "invalid gameId or version")
		return
	}
	if tmpName == "" {
		nw.fail(http.StatusBadRequest, "missing zip part")
		return
	}

	// Send start event after we know parameters
	fmt.Fprintf(nw, "{\"type\":\"start\",\"kind\":%q,\"gameId\":%q,\"version\":%q}\n", kind, gid, ver)
	fl.Flush()

	// Save zip to temp was already done (tmpName). Extract into a staging dir on
	// the same volume; the published directory is only replaced once the whole
	// build is on disk, so an aborted upload can never leave a half version live.
	finalVerDir := filepath.Join(h.root, "content", gid, ver)
	stageDir, filesRoot, err := h.stageVersionDir(gid, ver)
	if err != nil {
		nw.fail(http.StatusInternalServerError, err.Error())
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
			nw.fail(http.StatusInsufficientStorage, fmt.Sprintf("insufficient disk space: need %d bytes, have %d bytes", needBytes, freeBytes))
			return
		}
	}

	// Unzip with progress
	if !streamUnzip(nw, fl, tmpName, filesRoot) {
		return
	}
	// Remove temp zip
	os.Remove(tmpName)

	// Compose manifest with progress: pre-scan totals first
	totalFiles, totalBytes := countTree(filesRoot)
	fmt.Fprintf(nw, "{\"type\":\"composeStart\",\"totalFiles\":%d,\"totalBytes\":%d}\n", totalFiles, totalBytes)
	fl.Flush()

	files, emptyDirs, ok := streamCompose(nw, fl, filesRoot)
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
		streamError(nw, fl, "activate failed: "+err.Error())
		return
	}
	promoted = true

	outPath, _, err := h.writeManifest(m, upd)
	if err != nil {
		streamError(nw, fl, err.Error())
		return
	}

	fmt.Fprintf(nw, "{\"type\":\"done\",\"outPath\":%q}\n", outPath)
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
		info, err := d.Info()
		if err != nil {
			// The file vanished between the directory read and the stat (an
			// aborted upload being cleaned up, say). Ignoring the error and
			// calling info.Size() on a nil FileInfo panicked the handler; these
			// numbers only drive a progress bar, so skipping the entry is fine.
			return nil
		}
		totalFiles++
		totalBytes += info.Size()
		return nil
	})
	return totalFiles, totalBytes
}

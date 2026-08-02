package builds

import (
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"syscall"
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
		// The detail (a temp path, a disk error) goes to the log only; the
		// size and space guards are the exception, since an operator staring at
		// a rejected publish has to know whether the archive was too big or the
		// volume was too full.
		msg := "failed to read the upload"
		if isPublicUploadError(err) {
			msg = err.Error()
		}
		adminutil.Fail(w, code, msg, "upload", err)
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
		adminutil.Fail(w, http.StatusInternalServerError, "failed to prepare the staging directory", "upload", err)
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
		adminutil.Fail(w, http.StatusInternalServerError, "unzip failed", "upload", err)
		return
	}

	// Build manifest by scanning extracted files
	files, emptyDirs, err := scanManifest(filesRoot)
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to scan the extracted build", "upload", err)
		return
	}

	// Everything is extracted and hashed: publish the build in one rename.
	// The lock keeps a concurrent publication of the same version from
	// interleaving its content rename with our manifest write.
	unlock := lockPublish(gid, ver)
	defer unlock()
	if err := promoteVersionDir(stageDir, finalVerDir); err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "activate failed", "upload", err)
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
		adminutil.Fail(w, http.StatusInternalServerError, "failed to write the manifest", "upload", err)
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

// maxUploadZipBytes is the hard ceiling for the "zip" part of a publish
// request.
//
// The number is taken from deploy/launcher.conf, where every upload location
// sets client_max_body_size 30g: nginx already refuses anything bigger, and a
// lower value here would reject builds the platform is deliberately
// provisioned to accept (launcher builds are tens of MB, game builds run to
// several GB). The point of repeating the limit in the handler is the paths
// nginx does not cover — a request straight to :55777, or a chunked body with
// no Content-Length for the precheck to look at — so that no client can ever
// make us write an unbounded amount of data.
const maxUploadZipBytes = 30 << 30

// uploadZipLimit is the ceiling actually enforced. It exists as a variable
// purely so tests can lower it: streaming 30 GiB through a multipart reader to
// observe the refusal is not something a test suite can do.
var uploadZipLimit int64 = maxUploadZipBytes

// uploadFreeSpaceReserveBytes is the slack the upload guard leaves on the
// tmpDir volume. On production /srv is shared by the public API, the admin API
// and three other sites, so "the disk is exactly full" is already an outage:
// the upload has to give up while there is still room for the neighbours' logs
// and state files.
const uploadFreeSpaceReserveBytes = 1 << 30 // 1 GiB

// zipCopyBufferBytes is the copy buffer for the archive. A large buffer pays
// off on multi-gigabyte uploads.
const zipCopyBufferBytes = 4 << 20 // 4 MiB

// zipSpaceRecheckBytes is how often the copy re-measures free space. The
// precheck only knows the disk as it was when the request started; on a shared
// volume another writer (or a concurrent upload) can eat the room while we
// stream, so the budget is refreshed as we go.
const zipSpaceRecheckBytes = 256 << 20 // 256 MiB

// Guards that reject an upload for a reason the operator has to see: unlike a
// temp path or a raw disk error, these texts are safe to send back to the
// client and the admin UI needs to tell them apart.
var (
	errDuplicateZipPart = errors.New("duplicate zip part: a publish request must carry exactly one archive")
	errZipTooLarge      = errors.New("zip part exceeds the maximum upload size")
	errNoTempSpace      = errors.New("insufficient free space to spool the archive")
)

// isPublicUploadError reports whether err is one of the guards above, i.e.
// whether its message may be used as the HTTP response body.
func isPublicUploadError(err error) bool {
	return errors.Is(err, errDuplicateZipPart) || errors.Is(err, errZipTooLarge) || errors.Is(err, errNoTempSpace)
}

// freeSpaceFn is the free-space probe the upload guard uses. It is a variable
// only so tests can simulate a volume that is full or filling up, which cannot
// be arranged on a real machine.
var freeSpaceFn = freeSpaceBytes

// spoolZipPart copies the "zip" part into dst under two independent limits:
// the hard maxUploadZipBytes ceiling, and the space the tmpDir volume can
// actually still absorb. It returns the number of bytes written, the HTTP
// status to answer with, and the error.
//
// A volume we cannot measure does not block the upload: freeSpaceBytes fails
// on exotic filesystems and inside containers, and refusing every publish there
// would be a worse failure than the one being prevented. In that case only the
// byte ceiling applies and the reason is logged.
func spoolZipPart(dst io.Writer, src io.Reader, tmpDir string) (int64, int, error) {
	budget, budgetKnown := spaceBudget(tmpDir)
	if budgetKnown && budget == 0 {
		return 0, http.StatusInsufficientStorage, errNoTempSpace
	}

	buf := make([]byte, zipCopyBufferBytes)
	var total int64
	// budget describes the volume as of measuredAt bytes written, so it is
	// spent against total-measuredAt, not against total: measuring free space
	// again after writing 5 GB and then comparing it to all 5 GB would refuse
	// any upload larger than half the disk.
	measuredAt := int64(0)
	nextRecheck := int64(zipSpaceRecheckBytes)
	for {
		n, rerr := src.Read(buf)
		if n > 0 {
			if total+int64(n) > uploadZipLimit {
				return total, http.StatusRequestEntityTooLarge, errZipTooLarge
			}
			if budgetKnown && uint64(total-measuredAt)+uint64(n) > budget {
				return total, http.StatusInsufficientStorage, errNoTempSpace
			}
			if _, werr := dst.Write(buf[:n]); werr != nil {
				// A write that fails because the volume filled up anyway (the
				// budget is a snapshot, not a reservation) is a 507, not a 500.
				if errors.Is(werr, syscall.ENOSPC) {
					return total, http.StatusInsufficientStorage, errNoTempSpace
				}
				return total, http.StatusInternalServerError, werr
			}
			total += int64(n)
			if total >= nextRecheck {
				budget, budgetKnown = spaceBudget(tmpDir)
				if budgetKnown && budget == 0 {
					return total, http.StatusInsufficientStorage, errNoTempSpace
				}
				measuredAt = total
				nextRecheck = total + zipSpaceRecheckBytes
			}
		}
		if rerr == io.EOF {
			return total, http.StatusOK, nil
		}
		if rerr != nil {
			return total, http.StatusBadRequest, rerr
		}
	}
}

// spaceBudget returns how many bytes the volume behind dir may still be given,
// i.e. free space minus the reserve. The second result is false when the volume
// cannot be measured, which callers must treat as "no space limit known" rather
// than as "no space left".
func spaceBudget(dir string) (uint64, bool) {
	free, err := freeSpaceFn(dir)
	if err != nil {
		log.Printf("[builds] free space %s: %v (falling back to the byte ceiling alone)", dir, err)
		return 0, false
	}
	if free == 0 {
		// Some filesystems report 0 instead of failing; treating that as a
		// full disk would block every upload there.
		return 0, false
	}
	if free <= uploadFreeSpaceReserveBytes {
		return 0, true
	}
	return free - uploadFreeSpaceReserveBytes, true
}

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
			// A second "zip" part is refused rather than silently replacing the
			// first. The caller's cleanup defer only ever learns one temp name,
			// so overwriting out.tmpName orphaned the first spool file in
			// tmpDir permanently — a leak an attacker could repeat at will. No
			// legitimate client sends two archives in one publish request.
			if out.tmpName != "" {
				_ = part.Close()
				return out, http.StatusBadRequest, errDuplicateZipPart
			}
			if err := os.MkdirAll(tmpDir, 0o755); err != nil {
				_ = part.Close()
				return out, http.StatusInternalServerError, err
			}
			tmpZip, err := os.CreateTemp(tmpDir, "upload-*.zip")
			if err != nil {
				_ = part.Close()
				return out, http.StatusInternalServerError, err
			}
			// Recorded before the copy so that the caller removes the file on
			// every failure path below, including 413 and 507.
			out.tmpName = tmpZip.Name()
			out.filename = part.FileName()
			n, code, cerr := spoolZipPart(tmpZip, part, tmpDir)
			// Closed before returning: on Windows an open handle would make the
			// caller's os.Remove fail and leave the partial archive behind.
			_ = tmpZip.Close()
			if cerr != nil {
				return out, code, cerr
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

	// Optional precheck: ensure enough temp space based on Content-Length if
	// known. It only saves the client from streaming a body that is doomed —
	// a chunked request has no Content-Length at all, so the guard that
	// actually protects the volume is the one inside spoolZipPart.
	if r.ContentLength > 0 {
		if err := os.MkdirAll(h.tmpDir(), 0o755); err == nil {
			if free, ferr := freeSpaceFn(h.tmpDir()); ferr == nil && free > 0 && uint64(r.ContentLength) > free {
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
	// See lockPublish: promote and the manifest write must not interleave with
	// another publication of the same version.
	unlock := lockPublish(gid, ver)
	defer unlock()
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

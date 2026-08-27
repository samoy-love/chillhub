package builds

import (
	"errors"
	"fmt"
	"io"
	"log"
	"mime/multipart"
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
	parts, code, err := readUploadParts(r, h.tmpDir(), nil, nil)
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
	if kind == "launcher" {
		gid = "launcher"
	}
	if problem := missingPublishParam(kind, gid, ver); problem != "" {
		http.Error(w, problem, http.StatusBadRequest)
		return
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
	// The already-published check happens after extraction (below), once the
	// fresh content actually exists to compare — see the comment there for why.
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
	if msg := extractSpaceProblem(tmpName, filesRoot); msg != "" {
		http.Error(w, msg, http.StatusInsufficientStorage)
		return
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

	// The archive is already fully extracted and hashed at this point — the
	// same amount of work would have happened whether or not this version
	// turns out to already be published — so checking here rather than before
	// extraction costs nothing and is what makes a same-content re-upload
	// answerable at all: without the fresh manifest there would be nothing to
	// compare the published one against. promoted is still false, so the
	// deferred cleanup above removes stageDir without touching anything live.
	if h.respondLauncherRepublish(w, gid, ver, files, emptyDirs) {
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
	// The build is published; a client that hung up before reading the manifest
	// changes nothing and cannot be told anything either.
	_, _ = w.Write(b)
}

// respondLauncherRepublish answers a plain-multipart publish whose version is
// an already-published launcher build: it writes either the existing
// manifest (identical content) or the 409 conflict, and reports whether it
// wrote anything at all. false means the version isn't an already-published
// launcher build, and the caller should proceed to promote as normal.
//
// Pulled out of Upload as its own function (rather than left as inline ifs)
// purely to keep Upload's own cyclomatic complexity under the linter's
// ceiling — see the identical split for UploadStream and UploadProcessStream.
func (h *Handlers) respondLauncherRepublish(w http.ResponseWriter, gid, ver string, files []manifestFile, emptyDirs []string) bool {
	if !h.launcherVersionAlreadyPublished(gid, ver) {
		return false
	}
	if !h.launcherRepublishMatches(gid, ver, files, emptyDirs) {
		log.Printf("/admin/upload: refused: launcher version %s already published with different content", ver)
		http.Error(w, launcherVersionConflictMessage(ver), http.StatusConflict)
		return true
	}
	b, err := os.ReadFile(filepath.Join(h.manifestsDir(gid), ver+".json"))
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to read the published manifest", "upload", err)
		return true
	}
	log.Printf("/admin/upload: launcher version %s re-uploaded with identical content, no-op", ver)
	w.Header().Set("Content-Type", "application/json")
	_, _ = w.Write(b)
	return true
}

// extractSpaceProblem reports why an archive cannot be unpacked next to its
// destination, or "" when it fits — or cannot be judged.
//
// Neither unknown blocks a publish. The estimate comes from the archive headers
// and may be absent or hostile, and the free-space probe fails on exotic
// filesystems and inside containers; refusing every publish there would be a
// worse failure than the one being prevented. Extraction enforces its own byte
// budget regardless (see extractBudget).
//
// All three publish paths share it, and all three must: this is the guard that
// keeps a build from filling the volume the live content sits on.
func extractSpaceProblem(zipPath, filesRoot string) string {
	needBytes, err := estimateZipUncompressedSize(zipPath)
	if err != nil {
		return ""
	}
	freeBytes, ferr := freeSpaceFn(filesRoot)
	if ferr != nil || freeBytes == 0 || needBytes <= freeBytes {
		return ""
	}
	return fmt.Sprintf("insufficient disk space: need %d bytes, have %d bytes", needBytes, freeBytes)
}

// spoolSpaceProblem reports why a body of the announced length cannot be
// spooled, or "" when it fits or the answer is unknown.
//
// It only saves the client from streaming a body that is doomed: a chunked
// request has no Content-Length at all, so the guard that actually protects the
// volume is the one inside spoolZipPart.
func (h *Handlers) spoolSpaceProblem(contentLength int64) string {
	if contentLength <= 0 {
		return ""
	}
	// The announced body length is an int64 and free space a uint64, so one of
	// the two has to change type; contentLength is > 0 by the check above, which
	// makes this conversion exact.
	need := uint64(contentLength)
	if err := os.MkdirAll(h.tmpDir(), contentDirPerm); err != nil {
		return ""
	}
	free, ferr := freeSpaceFn(h.tmpDir())
	if ferr != nil || free == 0 || need <= free {
		return ""
	}
	return fmt.Sprintf("insufficient temp space: need %d bytes, have %d bytes", contentLength, free)
}

// missingPublishParam returns the message for the first mandatory publish
// parameter that is absent, or "" when all of them are present. Both publish
// entry points apply the same three rules in the same order.
func missingPublishParam(kind, gid, ver string) string {
	switch {
	case kind == "":
		return "missing kind (launcher|game)"
	case ver == "":
		return "missing version"
	case kind == "game" && strings.TrimSpace(gid) == "":
		return "missing gameId for kind=game"
	}
	return ""
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

// freeSpaceFn is the free-space probe every upload guard uses: the spool budget
// as well as the precheck each publish path runs before extraction. It is a
// variable only so tests can simulate a volume that is full or filling up, which
// cannot be arranged on a real machine — the guards must be called through it and
// never through freeSpaceBytes directly, or the branch becomes untestable.
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

	sp := &zipSpool{dst: dst, tmpDir: tmpDir, budget: budget, budgetKnown: budgetKnown, nextRecheck: zipSpaceRecheckBytes}
	buf := make([]byte, zipCopyBufferBytes)
	for {
		n, rerr := src.Read(buf)
		if n > 0 {
			if code, werr := sp.write(buf[:n]); werr != nil {
				return sp.total, code, werr
			}
		}
		if rerr == io.EOF {
			return sp.total, http.StatusOK, nil
		}
		if rerr != nil {
			return sp.total, http.StatusBadRequest, rerr
		}
	}
}

// zipSpool is the write side of spoolZipPart: the running byte count plus the
// free-space budget, so that the copy loop above stays a copy loop.
//
// budget describes the volume as it was at the last measurement, so it is spent
// against sinceMeasure and not against total: measuring free space again after
// writing 5 GB and then comparing it to all 5 GB would refuse any upload larger
// than half the disk.
type zipSpool struct {
	dst          io.Writer
	tmpDir       string
	total        int64
	budget       uint64
	budgetKnown  bool
	sinceMeasure uint64
	nextRecheck  int64
}

// write stores one chunk, refusing it if either limit would be crossed, and
// re-measures the volume every zipSpaceRecheckBytes. It returns the HTTP status
// to answer with alongside the error.
func (s *zipSpool) write(p []byte) (int, error) {
	n := len(p)
	if s.total+int64(n) > uploadZipLimit {
		return http.StatusRequestEntityTooLarge, errZipTooLarge
	}
	if s.budgetKnown && s.sinceMeasure+uint64(n) > s.budget {
		return http.StatusInsufficientStorage, errNoTempSpace
	}
	if _, werr := s.dst.Write(p); werr != nil {
		// A write that fails because the volume filled up anyway (the budget is
		// a snapshot, not a reservation) is a 507, not a 500.
		if errors.Is(werr, syscall.ENOSPC) {
			return http.StatusInsufficientStorage, errNoTempSpace
		}
		return http.StatusInternalServerError, werr
	}
	s.total += int64(n)
	s.sinceMeasure += uint64(n)
	if s.total >= s.nextRecheck {
		s.budget, s.budgetKnown = spaceBudget(s.tmpDir)
		if s.budgetKnown && s.budget == 0 {
			return http.StatusInsufficientStorage, errNoTempSpace
		}
		s.sinceMeasure = 0
		s.nextRecheck = s.total + zipSpaceRecheckBytes
	}
	return http.StatusOK, nil
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
// uploadProgress is told how many bytes of the archive have arrived. It is
// called from the spool loop, on the handler's own goroutine.
type uploadProgress func(received int64)

func readUploadParts(r *http.Request, tmpDir string, onZipProgress uploadProgress, onZipSaved func(filename string, n int64)) (*uploadParts, int, error) {
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
		case "kind":
			out.kind = strings.ToLower(field(part))
		case "gameId":
			out.gid = field(part)
		case "version":
			out.ver = field(part)
		case "updateLatest":
			out.updateLatest = field(part) == "1"
		case "zip":
			code, zerr := out.spoolZip(part, tmpDir, onZipProgress)
			if zerr != nil {
				_ = part.Close()
				return out, code, zerr
			}
			if onZipSaved != nil {
				onZipSaved(out.filename, out.saved)
			}
		default:
			// Consume but ignore unnamed and unknown fields. A read error here
			// is the body ending early, which the next NextPart reports anyway.
			_, _ = io.Copy(io.Discard, part)
		}
		_ = part.Close()
	}
	return out, http.StatusOK, nil
}

// spoolZip writes the "zip" part of a publish request to a temp file in tmpDir
// and records it on out. It returns the HTTP status to answer with.
func (out *uploadParts) spoolZip(part *multipart.Part, tmpDir string, onProgress uploadProgress) (int, error) {
	// A second "zip" part is refused rather than silently replacing the first.
	// The caller's cleanup defer only ever learns one temp name, so overwriting
	// out.tmpName orphaned the first spool file in tmpDir permanently — a leak
	// an attacker could repeat at will. No legitimate client sends two archives
	// in one publish request.
	if out.tmpName != "" {
		return http.StatusBadRequest, errDuplicateZipPart
	}
	if err := os.MkdirAll(tmpDir, contentDirPerm); err != nil {
		return http.StatusInternalServerError, err
	}
	tmpZip, err := os.CreateTemp(tmpDir, "upload-*.zip")
	if err != nil {
		return http.StatusInternalServerError, err
	}
	// Recorded before the copy so that the caller removes the file on every
	// failure path below, including 413 and 507.
	out.tmpName = tmpZip.Name()
	out.filename = part.FileName()
	var src io.Reader = part
	if onProgress != nil {
		src = &countingReader{r: part, on: onProgress}
	}
	n, code, cerr := spoolZipPart(tmpZip, src, tmpDir)
	// Closed before returning: on Windows an open handle would make the caller's
	// os.Remove fail and leave the partial archive behind.
	_ = tmpZip.Close()
	if cerr != nil {
		return code, cerr
	}
	out.saved = n
	return http.StatusOK, nil
}

// receivingEvery bounds how often the «архив идёт» event is repeated.
//
// Cloudflare wants to see the origin alive; it does not want a line per 32 KB
// buffer. Five seconds is twenty times faster than the hundred-second limit
// and costs a couple of dozen lines on a 68 MB upload.
var receivingEvery = 5 * time.Second

// countingReader reports progress as the body is read.
type countingReader struct {
	r    io.Reader
	on   uploadProgress
	seen int64
}

func (c *countingReader) Read(p []byte) (int, error) {
	n, err := c.r.Read(p)
	if n > 0 {
		c.seen += int64(n)
		c.on(c.seen)
	}
	return n, err
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
	if msg := h.spoolSpaceProblem(r.ContentLength); msg != "" {
		nw.fail(http.StatusInsufficientStorage, msg)
		return
	}

	// ОТВЕЧАТЬ НАДО СРАЗУ, А НЕ КОГДА ПРИЕДЕТ АРХИВ.
	//
	// Перед доменом стоит Cloudflare, и у него на ответ origin'а сто секунд.
	// Раньше первый байт ответа уходил только после того, как весь ZIP лёг во
	// временный файл: 68 МБ с раннера GitHub идут дольше, и выкатка получала
	// 524 — шесть раз подряд, по два с лишним гигабайта впустую. Здесь
	// отчёт о приёме идёт ПОКА тело читается, так что поток жив с первых
	// килобайт.
	//
	// Событие уходит не чаще, чем раз в receivingEvery: NDJSON на каждый
	// буфер — это мегабайты служебного трафика на ровном месте.
	lastBeat := time.Now()
	parts, code, err := readUploadParts(r, h.tmpDir(), func(received int64) {
		if time.Since(lastBeat) < receivingEvery {
			return
		}
		lastBeat = time.Now()
		emitEventf(nw, "{\"type\":\"receiving\",\"bytes\":%d}\n", received)
		fl.Flush()
	}, func(filename string, n int64) {
		emitEventf(nw, "{\"type\":\"zipSaved\",\"filename\":%q,\"bytes\":%d}\n", filename, n)
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
	if problem := missingPublishParam(kind, gid, ver); problem != "" {
		nw.fail(http.StatusBadRequest, problem)
		return
	}
	if !adminutil.IsSafeGameID(gid) || !adminutil.IsSafeVersion(ver) {
		nw.fail(http.StatusBadRequest, "invalid gameId or version")
		return
	}
	// The already-published check happens after compose (below), once the
	// fresh content actually exists to compare against the published manifest
	// — see the comment there for why, and see UploadInit for the one entry
	// point where that isn't possible.
	if tmpName == "" {
		nw.fail(http.StatusBadRequest, "missing zip part")
		return
	}

	// Send start event after we know parameters
	emitEventf(nw, "{\"type\":\"start\",\"kind\":%q,\"gameId\":%q,\"version\":%q}\n", kind, gid, ver)
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
	if msg := extractSpaceProblem(tmpName, filesRoot); msg != "" {
		nw.fail(http.StatusInsufficientStorage, msg)
		return
	}

	// Unzip with progress
	if !streamUnzip(nw, fl, tmpName, filesRoot) {
		return
	}
	// Remove the temp zip. The deferred cleanup above removes it too, so a
	// failure here is not worth reporting: it only means the janitor gets it.
	_ = os.Remove(tmpName)

	// Compose manifest with progress: pre-scan totals first
	totalFiles, totalBytes := countTree(filesRoot)
	emitEventf(nw, "{\"type\":\"composeStart\",\"totalFiles\":%d,\"totalBytes\":%d}\n", totalFiles, totalBytes)
	fl.Flush()

	files, emptyDirs, ok := streamCompose(nw, fl, filesRoot)
	if !ok {
		return
	}

	// Same reasoning as Upload: the zip was already fully spooled and
	// extracted before this point regardless of the outcome below, so
	// checking here — with the fresh manifest in hand — costs nothing extra
	// and is what lets an identical re-upload succeed instead of just failing
	// less usefully. promoted is still false, so the deferred cleanup removes
	// stageDir without touching the live version.
	if h.streamLauncherRepublish(nw, fl, gid, ver, files, emptyDirs, "[builds] uploadStream") {
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

	emitEventf(nw, "{\"type\":\"done\",\"outPath\":%q}\n", outPath)
	fl.Flush()
}

// streamLauncherRepublish is respondLauncherRepublish's NDJSON counterpart,
// used by UploadStream. chunked.go's UploadProcessStream has its own
// variant (processLauncherRepublish) because a match there also has to
// drop the now-redundant upload.zip and mark the chunked upload done —
// bookkeeping this function knows nothing about. logTag identifies the
// caller in the log line only; the emitted event is identical either way.
func (h *Handlers) streamLauncherRepublish(nw *ndjsonWriter, fl adminutil.Flusher, gid, ver string, files []manifestFile, emptyDirs []string, logTag string) bool {
	if !h.launcherVersionAlreadyPublished(gid, ver) {
		return false
	}
	if !h.launcherRepublishMatches(gid, ver, files, emptyDirs) {
		nw.fail(http.StatusConflict, launcherVersionConflictMessage(ver))
		return true
	}
	outPath := filepath.Join(h.manifestsDir(gid), ver+".json")
	log.Printf("%s: launcher version %s re-uploaded with identical content, no-op", logTag, ver)
	emitEventf(nw, "{\"type\":\"done\",\"outPath\":%q}\n", outPath)
	fl.Flush()
	return true
}

// countTree returns the number of files and their total size under root.
//
// The walk error is dropped on purpose: these two numbers only drive a progress
// bar, and the walk that actually builds the manifest reports its own failures.
func countTree(root string) (int, int64) {
	var totalFiles int
	var totalBytes int64
	_ = filepath.WalkDir(root, func(_ string, d os.DirEntry, err error) error {
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
			return nil //nolint:nilerr // a vanished file is skipped, not reported
		}
		totalFiles++
		totalBytes += info.Size()
		return nil
	})
	return totalFiles, totalBytes
}

// Package gamegallery serves the per-game screenshot gallery the launcher
// shows as a carousel: content/<gameId>/gallery/ for the pictures and
// content/<gameId>/gallery/gallery.json for their order, captions and cover.
//
// The handler shapes mirror server/internal/adminapi/news/assets.go on
// purpose — the gallery browser in the admin UI is the same widget pointed at
// a different root, so the two packages should not drift apart in behaviour.
// The one addition news/assets.go has no equivalent for is gallery.json
// itself (SetCaption, SetCover): every other admin gallery is a plain file
// tree, this one also carries carousel order and captions.
package gamegallery

import (
	"encoding/json"
	"errors"
	"io"
	"log"
	"maps"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"ChillHub/server/internal/adminapi/media"
	"ChillHub/server/internal/adminutil"
)

// errInvalidGameID reports a gameId that failed adminutil.IsSafeGameID or
// whose resolved path escaped the content root.
var errInvalidGameID = errors.New("invalid gameId")

// errEmptyFile reports a SetCaption/SetCover call with no file name.
var errEmptyFile = errors.New("empty file name")

// Image upload bounds, same as news assets: a screenshot is a picture like
// any other, so the same ceiling applies.
const (
	// MaxImageBytes caps one uploaded image.
	MaxImageBytes = media.MaxImageBytes
	// imageFormMemory is the in-RAM part of a multipart image upload.
	imageFormMemory = 8 << 20 // 8 MiB
)

// Handlers serves the game gallery endpoints for one content root.
type Handlers struct {
	root string
	// mu serialises the read-modify-write cycle on gallery.json, the same way
	// news.Handlers.mu guards news_meta.json: two admins setting a caption at
	// the same time must not interleave and drop one of the writes.
	mu sync.Mutex
}

// New returns handlers rooted at the given content directory.
func New(root string) *Handlers { return &Handlers{root: root} }

// contentBase is the "content" subtree the public API serves verbatim at
// /content/ (see cmd/api/main.go's PathPrefix("/content/")), as opposed to
// h.root itself, which holds manifests/ and news/. The gallery lives here so
// its files are reachable at /content/<gameId>/gallery/<file> without adding
// a new static route.
func (h *Handlers) contentBase() string { return filepath.Join(h.root, "content") }

// galleryRoot resolves content/<gameId>/gallery, validating gameId the same
// way every other path segment supplied by a request is validated: reject
// before it is ever joined into a path, not after.
func (h *Handlers) galleryRoot(gid string) (string, error) {
	if !adminutil.IsSafeGameID(gid) {
		return "", errInvalidGameID
	}
	base := h.contentBase()
	dir := filepath.Join(base, gid, "gallery")
	if !adminutil.EnsureWithin(base, dir) {
		return "", errInvalidGameID
	}
	return dir, nil
}

// requireGameID reads gameId from the request and resolves its gallery root,
// answering the request itself and reporting ok=false on failure.
func (h *Handlers) requireGameID(w http.ResponseWriter, gid string) (string, bool) {
	base, err := h.galleryRoot(gid)
	if err != nil {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return "", false
	}
	return base, true
}

// galleryItem is one row of the gallery browser.
type galleryItem struct {
	Name    string `json:"name"`
	URL     string `json:"url"`
	Size    int64  `json:"size"`
	ModTime string `json:"modTime"`
	IsDir   bool   `json:"isDir"`
}

// assetURL builds the web path of a gallery picture stored at rel/name under
// gameId's gallery. It matches the public API's PathPrefix("/content/"),
// which serves contentRoot/content verbatim.
func assetURL(gid, rel, name string) string {
	return "/content/" + gid + "/gallery/" +
		strings.TrimPrefix(filepath.ToSlash(filepath.Join(rel, name)), "/")
}

// galleryRow describes one directory entry for the browser. Directories carry
// no URL, size or time: they are navigated into, not linked to.
func galleryRow(e os.DirEntry, gid, rel string) galleryItem {
	if e.IsDir() {
		return galleryItem{Name: e.Name(), IsDir: true}
	}
	row := galleryItem{Name: e.Name(), URL: assetURL(gid, rel, e.Name())}
	if info, err := e.Info(); err == nil {
		row.Size = info.Size()
		row.ModTime = info.ModTime().UTC().Format(time.RFC3339)
	}
	return row
}

// isGalleryRoot reports whether p is the gallery root itself. See
// news.isAssetsRoot: the same reasoning applies — deleting or renaming the
// root would take out every screenshot of the game in one request.
func isGalleryRoot(base, p string) bool {
	return filepath.Clean(p) == filepath.Clean(base)
}

// List returns the contents of a directory under content/<gameId>/gallery for
// the gallery browser (GET gameId, path).
func (h *Handlers) List(w http.ResponseWriter, r *http.Request) {
	gid := r.URL.Query().Get("gameId")
	base, ok := h.requireGameID(w, gid)
	if !ok {
		return
	}
	rel := adminutil.SanitizeAssetPath(r.URL.Query().Get("path"))
	dir := filepath.Join(base, rel)
	if !adminutil.EnsureWithin(base, dir) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		// %q, not %s: SanitizeAssetPath leaves control characters alone, and the
		// error text embeds the absolute content root — see news.AssetsList.
		log.Printf("[gamegallery] list %q: %v", dir, err)
		http.Error(w, "directory not found", http.StatusNotFound)
		return
	}
	q := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("q")))
	dirsOnly := r.URL.Query().Get("dirsOnly") == "1"
	out := struct {
		Path  string        `json:"path"`
		Items []galleryItem `json:"items"`
	}{Path: filepath.ToSlash(rel), Items: []galleryItem{}}
	for _, e := range entries {
		if dirsOnly && !e.IsDir() {
			continue
		}
		if q != "" && !strings.Contains(strings.ToLower(e.Name()), q) {
			continue
		}
		out.Items = append(out.Items, galleryRow(e, gid, rel))
	}
	sort.Slice(out.Items, func(i, j int) bool {
		if out.Items[i].IsDir != out.Items[j].IsDir {
			return out.Items[i].IsDir && !out.Items[j].IsDir
		}
		return out.Items[i].ModTime > out.Items[j].ModTime
	})
	adminutil.WriteJSON(w, out)
}

// Mkdir creates a directory in the gallery (POST gameId, path, name).
func (h *Handlers) Mkdir(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	base, ok := h.requireGameID(w, r.FormValue("gameId"))
	if !ok {
		return
	}
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	// Checked before sanitising: see news.AssetsMkdir.
	name := strings.TrimSpace(r.FormValue("name"))
	if name == "" {
		http.Error(w, "empty name", http.StatusBadRequest)
		return
	}
	name = adminutil.SanitizeFilename(name)
	dir := filepath.Join(base, rel, name)
	if !adminutil.EnsureWithin(base, dir) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	// #nosec G301 -- the gallery is served straight from disk; 0750 would make
	// it unreadable to the process that serves it.
	if err := os.MkdirAll(dir, 0o755); err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to create the directory", "gamegallery", err)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// Upload accepts a multipart image, converts it and stores it (POST gameId,
// path, filename, file).
func (h *Handlers) Upload(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, MaxImageBytes+(1<<20))
	// #nosec G120 -- the body is bounded by the MaxBytesReader above.
	if err := r.ParseMultipartForm(imageFormMemory); err != nil {
		http.Error(w, "request too large or malformed", http.StatusBadRequest)
		return
	}
	base, ok := h.requireGameID(w, r.FormValue("gameId"))
	if !ok {
		return
	}
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	desired := adminutil.SanitizeFilename(r.FormValue("filename"))
	f, hdr, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file", http.StatusBadRequest)
		return
	}
	defer func() { _ = f.Close() }()
	data, err := io.ReadAll(io.LimitReader(f, MaxImageBytes+1))
	if err != nil {
		log.Printf("[gamegallery] read upload: %v", err)
		http.Error(w, "failed to read upload", http.StatusInternalServerError)
		return
	}
	if len(data) > MaxImageBytes {
		http.Error(w, "image too large", http.StatusRequestEntityTooLarge)
		return
	}
	inName := strings.ToLower(hdr.Filename)
	extHint := filepath.Ext(inName)
	if desired == "" {
		desired = strings.TrimSuffix(adminutil.SanitizeFilename(hdr.Filename), extHint)
	} else {
		desired = strings.TrimSuffix(desired, filepath.Ext(desired))
	}
	outName, metaFields, err := media.ProcessAndSaveAsset(base, rel, desired, data, extHint, "")
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to process the image", "gamegallery", err)
		return
	}
	resp := map[string]string{"status": "ok", "url": assetURL(r.FormValue("gameId"), rel, outName), "filename": outName}
	maps.Copy(resp, metaFields)
	adminutil.WriteJSON(w, resp)
}

// UploadByURL downloads an image and processes it like a file upload (POST
// gameId, path, filename, url).
func (h *Handlers) UploadByURL(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	base, ok := h.requireGameID(w, r.FormValue("gameId"))
	if !ok {
		return
	}
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	desired := adminutil.SanitizeFilename(r.FormValue("filename"))
	if desired != "" {
		desired = strings.TrimSuffix(desired, filepath.Ext(desired))
	}
	srcURL := strings.TrimSpace(r.FormValue("url"))
	if srcURL == "" {
		http.Error(w, "empty url", http.StatusBadRequest)
		return
	}
	data, ct, err := media.DownloadURL(r.Context(), srcURL)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	extHint := strings.ToLower(filepath.Ext(strings.Split(strings.Split(srcURL, "?")[0], "#")[0]))
	outName, metaFields, err := media.ProcessAndSaveAsset(base, rel, desired, data, extHint, ct)
	if err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to process the image", "gamegallery", err)
		return
	}
	resp := map[string]string{"status": "ok", "url": assetURL(r.FormValue("gameId"), rel, outName), "filename": outName}
	maps.Copy(resp, metaFields)
	adminutil.WriteJSON(w, resp)
}

// Delete removes a file or directory from the gallery (POST gameId, path,
// name).
func (h *Handlers) Delete(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	base, ok := h.requireGameID(w, r.FormValue("gameId"))
	if !ok {
		return
	}
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	name := strings.TrimSpace(r.FormValue("name"))
	if name == "" {
		http.Error(w, "empty name", http.StatusBadRequest)
		return
	}
	name = adminutil.SanitizeFilename(name)
	target := filepath.Join(base, rel, name)
	if !adminutil.EnsureWithin(base, target) || isGalleryRoot(base, target) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	// Lstat, not Stat, so a dangling symlink can still be cleaned up; see
	// news.AssetsDelete for why a missing target must be a 404, not a silent 200.
	// #nosec G703 -- target is SanitizeFilename output joined onto the gallery
	// and confirmed by EnsureWithin to stay inside it, and it is not the root.
	if _, err := os.Lstat(target); err != nil {
		http.Error(w, "not found", http.StatusNotFound)
		return
	}
	// #nosec G703 -- see the check above.
	if err := os.RemoveAll(target); err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to delete", "gamegallery", err)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// Rename renames a file or directory in the gallery (POST gameId, path,
// from, to).
func (h *Handlers) Rename(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	base, ok := h.requireGameID(w, r.FormValue("gameId"))
	if !ok {
		return
	}
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	from := strings.TrimSpace(r.FormValue("from"))
	to := strings.TrimSpace(r.FormValue("to"))
	if from == "" || to == "" {
		http.Error(w, "empty names", http.StatusBadRequest)
		return
	}
	from = adminutil.SanitizeFilename(from)
	to = adminutil.SanitizeFilename(to)
	src := filepath.Join(base, rel, from)
	dst := filepath.Join(base, rel, to)
	if !adminutil.EnsureWithin(base, src) || !adminutil.EnsureWithin(base, dst) ||
		isGalleryRoot(base, src) || isGalleryRoot(base, dst) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	// #nosec G703 -- both ends are SanitizeFilename output joined onto the
	// gallery, confirmed by EnsureWithin to stay inside it and not to be its root.
	if err := os.Rename(src, dst); err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to rename", "gamegallery", err)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// galleryFile is the JSON shape of content/<gameId>/gallery/gallery.json.
type galleryFile struct {
	Cover string            `json:"cover"`
	Items []galleryFileItem `json:"items"`
}

// galleryFileItem is one carousel entry. Order in the slice is carousel
// order, so callers that reorder items must rewrite the whole slice — this
// package only ever appends or edits in place.
type galleryFileItem struct {
	File    string `json:"file"`
	Caption string `json:"caption"`
}

// galleryJSONPath returns the path of gallery.json for gid, without touching
// the disk.
func (h *Handlers) galleryJSONPath(gid string) (string, error) {
	dir, err := h.galleryRoot(gid)
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "gallery.json"), nil
}

// readGalleryFile loads gallery.json, treating "does not exist yet" as an
// empty gallery rather than an error: SetCaption and SetCover are the first
// writers of this file, and a fresh game has none yet.
func readGalleryFile(p string) (galleryFile, error) {
	// #nosec G304 -- p is built by galleryJSONPath from a validated gameId.
	b, err := os.ReadFile(p)
	if err != nil {
		if os.IsNotExist(err) {
			return galleryFile{Items: []galleryFileItem{}}, nil
		}
		return galleryFile{}, err
	}
	var gf galleryFile
	if err := json.Unmarshal(b, &gf); err != nil {
		return galleryFile{}, err
	}
	if gf.Items == nil {
		gf.Items = []galleryFileItem{}
	}
	return gf, nil
}

// writeGalleryFile stores gf atomically: gallery.json is read by the public
// API, so a truncating write must never be observable.
func writeGalleryFile(p string, gf galleryFile) error {
	b, err := json.MarshalIndent(gf, "", "  ")
	if err != nil {
		return err
	}
	return adminutil.WriteFileAtomic(p, b, 0o644)
}

// SetCover sets the cover picture of gid's gallery to file, creating
// gallery.json if it does not exist yet. It does not require file to already
// be listed in items: the cover is picked from the same directory but is a
// separate concept, and requiring an items entry first would make the admin
// UI set the caption before the cover for no reason.
func (h *Handlers) SetCover(gameID, file string) error {
	if !adminutil.IsSafeGameID(gameID) {
		return errInvalidGameID
	}
	// Checked BEFORE sanitising: SanitizeFilename never returns "" (an empty
	// name comes back as "file"), so this must run first or an empty request
	// silently addresses a real, unintended entry — see news.AssetsMkdir.
	if strings.TrimSpace(file) == "" {
		return errEmptyFile
	}
	file = adminutil.SanitizeFilename(strings.TrimSpace(file))
	p, err := h.galleryJSONPath(gameID)
	if err != nil {
		return err
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	gf, err := readGalleryFile(p)
	if err != nil {
		return err
	}
	gf.Cover = file
	return writeGalleryFile(p, gf)
}

// SetCaption sets the caption of file in gid's gallery, creating gallery.json
// if it does not exist yet.
//
// If file has no entry in items yet — a picture uploaded straight into the
// directory before anyone captioned it, or gallery.json missing entirely —
// SetCaption APPENDS it rather than failing. The alternative (require a
// separate "add to gallery" call first) would let the admin UI's caption box
// silently no-op on a picture it has not registered yet; appending here means
// setting a caption is always enough to make an item exist. New items always
// land at the end of the carousel order, matching upload order until an
// explicit reorder request is added.
func (h *Handlers) SetCaption(gameID, file, caption string) error {
	if !adminutil.IsSafeGameID(gameID) {
		return errInvalidGameID
	}
	// Checked BEFORE sanitising: SanitizeFilename never returns "" (an empty
	// name comes back as "file"), so this must run first or an empty request
	// silently addresses a real, unintended entry — see news.AssetsMkdir.
	if strings.TrimSpace(file) == "" {
		return errEmptyFile
	}
	file = adminutil.SanitizeFilename(strings.TrimSpace(file))
	p, err := h.galleryJSONPath(gameID)
	if err != nil {
		return err
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	gf, err := readGalleryFile(p)
	if err != nil {
		return err
	}
	found := false
	for i := range gf.Items {
		if gf.Items[i].File == file {
			gf.Items[i].Caption = caption
			found = true
			break
		}
	}
	if !found {
		gf.Items = append(gf.Items, galleryFileItem{File: file, Caption: caption})
	}
	return writeGalleryFile(p, gf)
}

// SetCoverHandler is the HTTP entry point for SetCover (POST gameId, file).
func (h *Handlers) SetCoverHandler(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	if err := h.SetCover(r.FormValue("gameId"), r.FormValue("file")); err != nil {
		respondGalleryJSONErr(w, err)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// SetCaptionHandler is the HTTP entry point for SetCaption (POST gameId,
// file, caption).
func (h *Handlers) SetCaptionHandler(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "malformed form", http.StatusBadRequest)
		return
	}
	if err := h.SetCaption(r.FormValue("gameId"), r.FormValue("file"), r.FormValue("caption")); err != nil {
		respondGalleryJSONErr(w, err)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// respondGalleryJSONErr answers a SetCover/SetCaption failure: a bad gameId
// or empty file name is the caller's fault (400); anything else is a disk
// error worth hiding, like every other handler in this package.
func respondGalleryJSONErr(w http.ResponseWriter, err error) {
	if errors.Is(err, errInvalidGameID) || errors.Is(err, errEmptyFile) {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	adminutil.Fail(w, http.StatusInternalServerError, "failed to update the gallery", "gamegallery", err)
}

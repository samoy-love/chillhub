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
	"path"
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
		// Корня галереи ещё нет — у игры просто ни одной картинки. Это пустая
		// галерея, а не ошибка: папку заводит первая загрузка, а до неё панель
		// показывала «HTTP 404» на совершенно исправной новой игре.
		//
		// Подпапка — другое дело: туда переходят осознанно, и если её нет, то
		// список устарел, о чём честнее сказать 404, чем показать пустоту.
		if os.IsNotExist(err) && rel == "" {
			adminutil.WriteJSON(w, struct {
				Path  string        `json:"path"`
				Items []galleryItem `json:"items"`
			}{Path: "", Items: []galleryItem{}})
			return
		}

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
	// The manifest has to follow the disk. Without this, deleting the cover
	// left gallery.json pointing at a file that is gone: the request answered
	// 200, the admin saw the picture disappear, and the launcher went on
	// fetching a 404 for the витрина.
	if err := h.forgetRef(r.FormValue("gameId"), joinRef(rel, name)); err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to update the gallery", "gamegallery", err)
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
	// Same reason as in Delete: a renamed cover that keeps its old name in
	// gallery.json is a cover pointing at nothing.
	if err := h.moveRef(r.FormValue("gameId"), joinRef(rel, from), joinRef(rel, to)); err != nil {
		adminutil.Fail(w, http.StatusInternalServerError, "failed to update the gallery", "gamegallery", err)
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

// galleryRef normalises a gallery.json "file" reference: a path relative to
// the gallery root, with forward slashes, that stays inside it.
//
// It is NOT adminutil.SanitizeFilename. That one folds '/' into '_', so a
// picture the browser had navigated into ("shots/moon.png") was recorded as
// the non-existent "shots_moon.png" — the request answered 200 and the cover
// silently pointed at nothing. The gallery browser can create and enter
// subdirectories, so references have to survive them.
//
// The directory part goes through SanitizeAssetPath (which strips "..") and
// only the last segment through SanitizeFilename, then the joined path is
// checked against the root the same way every other path in this package is.
func galleryRef(base, ref string) (string, error) {
	ref = strings.ReplaceAll(strings.TrimSpace(ref), "\\", "/")
	if ref == "" {
		return "", errEmptyFile
	}
	dir, name := path.Split(strings.Trim(ref, "/"))
	// Checked BEFORE sanitising: SanitizeFilename never returns "" (an empty
	// name comes back as "file"), so an empty request would otherwise silently
	// address a real, unintended entry — see news.AssetsMkdir.
	if strings.TrimSpace(name) == "" {
		return "", errEmptyFile
	}
	dir = adminutil.SanitizeAssetPath(dir)
	name = adminutil.SanitizeFilename(strings.TrimSpace(name))
	rel := name
	if dir != "" {
		rel = dir + "/" + name
	}
	if !adminutil.EnsureWithin(base, filepath.Join(base, filepath.FromSlash(rel))) {
		return "", errInvalidGameID
	}
	return rel, nil
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
// gallery.json if it does not exist yet, and REGISTERS file in items when it
// is not listed there yet.
//
// The registration is the whole point. The launcher builds its carousel from
// items and reads cover only to decide which of those entries comes first
// (see GalleryClient.ParseManifest): a gallery.json carrying a cover and an
// empty items list is an empty gallery to it. While SetCover wrote cover
// alone, the admin flow "upload a picture, press «Сделать обложкой»" produced
// exactly that file, answered 200, lit the «Обложка» badge — and the launcher
// showed no cover at all. Setting a caption first happened to fix it, because
// SetCaption appends; nothing said so anywhere.
func (h *Handlers) SetCover(gameID, file string) error {
	base, err := h.galleryRoot(gameID)
	if err != nil {
		return err
	}
	file, err = galleryRef(base, file)
	if err != nil {
		return err
	}
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
	if !hasItem(gf.Items, file) {
		gf.Items = append(gf.Items, galleryFileItem{File: file})
	}
	return writeGalleryFile(p, gf)
}

// joinRef builds the gallery.json reference of name inside directory rel, in
// the slash form the manifest and the launcher's URLs both use.
func joinRef(rel, name string) string {
	if rel == "" {
		return name
	}
	return strings.Trim(filepath.ToSlash(filepath.Join(rel, name)), "/")
}

// underRef reports whether item is ref itself or lives beneath it. Deleting or
// renaming a directory has to carry every picture inside it, not just an entry
// that happens to equal the directory name.
func underRef(item, ref string) bool {
	return item == ref || strings.HasPrefix(item, ref+"/")
}

// forgetRef drops ref (and anything beneath it, when ref is a directory) from
// gallery.json, clearing cover if it pointed at a dropped entry. A gallery
// without a manifest yet is left alone rather than written empty: there is
// nothing to forget.
func (h *Handlers) forgetRef(gameID, ref string) error {
	return h.editGallery(gameID, func(gf *galleryFile) {
		kept := gf.Items[:0]
		for _, it := range gf.Items {
			if !underRef(it.File, ref) {
				kept = append(kept, it)
			}
		}
		gf.Items = kept
		if underRef(gf.Cover, ref) {
			gf.Cover = ""
		}
	})
}

// moveRef rewrites references from oldRef to newRef, keeping captions and
// carousel position. Entries beneath a renamed directory move with it.
func (h *Handlers) moveRef(gameID, oldRef, newRef string) error {
	return h.editGallery(gameID, func(gf *galleryFile) {
		rewrite := func(s string) string {
			switch {
			case s == oldRef:
				return newRef
			case strings.HasPrefix(s, oldRef+"/"):
				return newRef + strings.TrimPrefix(s, oldRef)
			default:
				return s
			}
		}
		for i := range gf.Items {
			gf.Items[i].File = rewrite(gf.Items[i].File)
		}
		gf.Cover = rewrite(gf.Cover)
	})
}

// editGallery runs edit against gid's manifest under the same lock every other
// writer takes, and writes the result back only when the manifest already
// exists — a game whose gallery was never captioned has no gallery.json, and
// deleting a picture from it must not create one.
func (h *Handlers) editGallery(gameID string, edit func(*galleryFile)) error {
	p, err := h.galleryJSONPath(gameID)
	if err != nil {
		return err
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if _, err := os.Stat(p); err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	gf, err := readGalleryFile(p)
	if err != nil {
		return err
	}
	edit(&gf)
	return writeGalleryFile(p, gf)
}

// hasItem reports whether items already lists file.
func hasItem(items []galleryFileItem, file string) bool {
	for i := range items {
		if items[i].File == file {
			return true
		}
	}
	return false
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
	base, err := h.galleryRoot(gameID)
	if err != nil {
		return err
	}
	file, err = galleryRef(base, file)
	if err != nil {
		return err
	}
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

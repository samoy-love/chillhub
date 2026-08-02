// Package news manages the markdown articles served to the launcher, their
// index.json, the per-article metadata file and the shared asset gallery.
//
// Every path built from request input goes through the guards in adminutil:
// scope/gameId via Base, slugs via adminutil.NewsSlugPath.
package news

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"ChillHub/server/internal/adminutil"
)

// The scope errors. They reach the admin panel as the body of a 400, so the
// wording is what the operator reads; they are sentinels so that a caller can
// tell them apart without matching on that text.
var (
	errGameIDRequired = errors.New("gameId required for scope=game")
	errInvalidGameID  = errors.New("invalid gameId")
	errInvalidScope   = errors.New("invalid scope")
)

// maxArticleBytes bounds one save or preview request body.
//
// The payload is markdown text, so tens of megabytes is already far past
// anything the editor can produce. Without a bound ParseMultipartForm keeps its
// parse window in RAM and spools the REST of the body to a temp file with no
// ceiling at all, so one request could fill the disk.
const maxArticleBytes = 32 << 20

// formBool reads the truthy spellings the admin UI sends for a checkbox.
func formBool(v string) bool {
	switch strings.TrimSpace(strings.ToLower(v)) {
	case "true", "1", "yes":
		return true
	}
	return false
}

// Handlers serves the news endpoints for one content root.
type Handlers struct {
	root string
	// mu serialises the read-modify-write cycles on news_meta.json and the two
	// index.json files, like feedback and metrics do for their stores. Two admins
	// saving at the same time used to interleave — the second writer read the
	// metadata before the first had written it and silently dropped its change.
	mu sync.Mutex
}

// New returns handlers rooted at the given content directory.
func New(root string) *Handlers { return &Handlers{root: root} }

// newsRoot is the directory every public news path must stay inside. Its whole
// contents are served to the world by nginx (and by the dev static handler in
// cmd/api), so nothing unpublished may be written here.
func (h *Handlers) newsRoot() string { return filepath.Join(h.root, "news") }

// privateNewsRoot is the sibling tree no web server maps. Drafts and
// news_meta.json live here; see the dirs type in meta.go.
func (h *Handlers) privateNewsRoot() string { return filepath.Join(h.root, "news_private") }

// assetsRoot is the shared image gallery.
func (h *Handlers) assetsRoot() string { return filepath.Join(h.root, "news", "assets") }

// Base resolves the news base directory by scope and optional gameId.
// gid comes straight from the request, so it is validated and the resulting
// path is re-checked against the news root to rule out traversal.
func (h *Handlers) Base(scope, gid string) (string, error) {
	root := h.newsRoot()
	if scope == "launcher" {
		return root, nil
	}
	if scope == "game" {
		if strings.TrimSpace(gid) == "" {
			return "", errGameIDRequired
		}
		if !adminutil.IsSafeGameID(gid) {
			return "", errInvalidGameID
		}
		p := filepath.Join(root, "games", gid)
		if !adminutil.EnsureWithin(root, p) {
			return "", errInvalidGameID
		}
		return p, nil
	}
	return "", fmt.Errorf("%w: %s", errInvalidScope, scope)
}

// dirs resolves both the public and the private directory of a scope. Every
// handler works with the pair so that a draft can never be written into the
// served tree by accident.
func (h *Handlers) dirs(scope, gid string) (dirs, error) {
	pub, err := h.Base(scope, gid)
	if err != nil {
		return dirs{}, err
	}
	priv := h.privateNewsRoot()
	if scope == "game" {
		priv = filepath.Join(priv, "games", gid)
		if !adminutil.EnsureWithin(h.privateNewsRoot(), priv) {
			return dirs{}, errInvalidGameID
		}
	}
	return dirs{pub: pub, priv: priv}, nil
}

// articlePath returns the markdown path of slug in the directory that matches
// its published state.
func articlePath(d dirs, slug string, published bool) (string, error) {
	if published {
		return adminutil.NewsSlugPath(d.pub, slug)
	}
	return adminutil.NewsSlugPath(d.priv, slug)
}

// findArticle locates the markdown of slug, wherever it currently lives.
//
// Both candidates come out of adminutil.NewsSlugPath, which rejects the slug
// unless it passes IsSafeNewsSlug and the joined path stays inside the base.
func findArticle(d dirs, slug string) (string, bool, error) {
	pubPath, err := adminutil.NewsSlugPath(d.pub, slug)
	if err != nil {
		return "", false, err
	}
	// #nosec G703 -- pubPath was just validated by NewsSlugPath.
	if _, err := os.Stat(pubPath); err == nil {
		return pubPath, true, nil
	}
	privPath, err := adminutil.NewsSlugPath(d.priv, slug)
	if err != nil {
		return "", false, err
	}
	// #nosec G703 -- privPath was just validated by NewsSlugPath.
	if _, err := os.Stat(privPath); err == nil {
		return privPath, false, nil
	}
	return "", false, os.ErrNotExist
}

// writeArticle stores a markdown body at p, which articlePath resolved.
func writeArticle(p, md string) error {
	// #nosec G301 -- content/news is handed out by nginx, which runs as a
	// different user than the API; 0750 would make it unreadable.
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		return err
	}
	// Atomic: a published article lands in the tree the launcher reads, and a
	// truncating write hands a reader in between a half-written markdown file.
	return adminutil.WriteFileAtomic(p, []byte(md), 0o644)
}

// errRemoveFailed marks a deletion that failed on a file that does exist, as
// opposed to a slug the caller spelled wrong.
var errRemoveFailed = errors.New("failed to remove article file")

// removeArticleFiles drops the markdown of slug from both directories and
// reports whether anything was actually there. The article may sit on either
// side, so both candidates are tried and only a file that exists and refuses to
// go away is an error.
func removeArticleFiles(d dirs, slug string) (bool, error) {
	removed := false
	for _, published := range []bool{true, false} {
		p, err := articlePath(d, slug, published)
		if err != nil {
			return false, err
		}
		// #nosec G703 -- p comes from articlePath, i.e. from NewsSlugPath.
		err = os.Remove(p)
		switch {
		case err == nil:
			removed = true
		case !os.IsNotExist(err):
			return false, fmt.Errorf("%w %q: %w", errRemoveFailed, p, err)
		}
	}
	return removed, nil
}

// moveFile relocates src onto dst, replacing dst if it exists (os.Rename
// refuses to overwrite on Windows).
//
// Both paths are produced by articlePath, i.e. by adminutil.NewsSlugPath, so
// neither can point outside the two news directories.
func moveFile(src, dst string) error {
	if src == dst {
		return nil
	}
	// #nosec G301 -- content/news is handed out by nginx, which runs as a
	// different user than the API; 0750 would make it unreadable.
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	// #nosec G703 -- src and dst come from articlePath, see the doc comment.
	if err := os.Rename(src, dst); err == nil {
		return nil
	}
	// #nosec G304 -- src comes from articlePath, see the doc comment.
	b, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	// Atomic: dst may be inside the publicly served news tree.
	if err := adminutil.WriteFileAtomic(dst, b, 0o644); err != nil {
		return err
	}
	// #nosec G703 -- src comes from articlePath, see the doc comment.
	return os.Remove(src)
}

// List returns the admin index (drafts included) for the requested scope.
func (h *Handlers) List(w http.ResponseWriter, r *http.Request) {
	scope := r.URL.Query().Get("scope")
	gid := r.URL.Query().Get("gameId")
	d, err := h.dirs(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	b, err := os.ReadFile(adminIndexPath(d))
	if err != nil {
		// First call after the drafts moved out of the public tree: build the
		// admin index (and migrate any draft still sitting in it) on demand.
		if rerr := RebuildIndex(d); rerr == nil {
			b, err = os.ReadFile(adminIndexPath(d))
		}
	}
	if err != nil {
		http.Error(w, "news index not available", http.StatusNotFound)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_, _ = w.Write(b)
}

// Get returns the markdown of one slug together with its metadata.
func (h *Handlers) Get(w http.ResponseWriter, r *http.Request) {
	scope := r.URL.Query().Get("scope")
	gid := r.URL.Query().Get("gameId")
	slug := r.URL.Query().Get("slug")
	if slug == "" {
		http.Error(w, "missing slug", http.StatusBadRequest)
		return
	}
	if !adminutil.IsSafeNewsSlug(slug) {
		http.Error(w, "invalid slug", http.StatusBadRequest)
		return
	}
	d, err := h.dirs(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	p, _, err := findArticle(d, slug)
	if err != nil {
		http.Error(w, "article not found", http.StatusNotFound)
		return
	}
	// #nosec G304 -- p comes from findArticle, i.e. from NewsSlugPath.
	b, err := os.ReadFile(p)
	if err != nil {
		http.Error(w, "article not found", http.StatusNotFound)
		return
	}
	meta := readMeta(d)[slug]
	w.Header().Set("Content-Type", "application/json")
	adminutil.WriteJSON(w, map[string]any{"markdown": string(b), "published": meta.Published, "coverUrl": meta.CoverURL})
}

// Save writes the markdown for a slug, updates metadata and rebuilds the index.
func (h *Handlers) Save(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	// Bound the whole request before parsing: ParseMultipartForm spools whatever
	// does not fit its window to a temp file, with no ceiling of its own.
	r.Body = http.MaxBytesReader(w, r.Body, maxArticleBytes)
	// #nosec G120 -- the body is bounded by the MaxBytesReader above.
	if err := r.ParseMultipartForm(16 << 20); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	scope := r.FormValue("scope")
	gid := r.FormValue("gameId")
	slug := r.FormValue("slug")
	md := r.FormValue("markdown")
	cov := strings.TrimSpace(r.FormValue("coverUrl"))
	pub := formBool(r.FormValue("published"))
	if slug == "" {
		http.Error(w, "missing slug", http.StatusBadRequest)
		return
	}
	if !adminutil.IsSafeNewsSlug(slug) {
		http.Error(w, "invalid slug", http.StatusBadRequest)
		return
	}
	d, err := h.dirs(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	// The whole read-modify-write of the metadata plus both indexes is one
	// critical section; a concurrent Save must not observe or overwrite it
	// half-done.
	h.mu.Lock()
	defer h.mu.Unlock()
	// update meta if provided
	m := readMeta(d)
	cur := m[slug]
	if cov != "" {
		cur.CoverURL = cov
	}
	// honor explicit published flag in save (if not provided, leave as-is)
	if r.Form.Has("published") {
		cur.Published = pub
	}
	m[slug] = cur
	// The markdown goes straight into the directory its published state calls
	// for; a draft must never touch the served tree, not even briefly.
	p, err := articlePath(d, slug, cur.Published)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if err := writeArticle(p, md); err != nil {
		log.Printf("[news:save] write %q: %v", p, err)
		http.Error(w, "failed to store article", http.StatusInternalServerError)
		return
	}
	// Drop the copy in the other directory, if the article moved.
	if other, oerr := articlePath(d, slug, !cur.Published); oerr == nil {
		// #nosec G703 -- other comes from articlePath, i.e. from NewsSlugPath.
		_ = os.Remove(other)
	}
	// The markdown is on disk; if the metadata does not follow it, the published
	// flag and the cover are lost and the next RebuildIndex resurrects the old
	// state — while the client was told everything went fine.
	if err := writeMeta(d, m); err != nil {
		log.Printf("[news:save] write meta: %v", err)
		http.Error(w, "saved but metadata update failed", http.StatusInternalServerError)
		return
	}
	if err := RebuildIndex(d); err != nil {
		log.Printf("[news:save] rebuild index: %v", err)
		http.Error(w, "saved but index rebuild failed", http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok", "slug": slug})
}

// Delete removes the markdown and its meta entry, then rebuilds the index.
func (h *Handlers) Delete(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	scope := r.URL.Query().Get("scope")
	gid := r.URL.Query().Get("gameId")
	slug := r.URL.Query().Get("slug")
	if strings.TrimSpace(slug) == "" {
		http.Error(w, "missing slug", http.StatusBadRequest)
		return
	}
	d, err := h.dirs(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	removed, err := removeArticleFiles(d, slug)
	if err != nil {
		if errors.Is(err, errRemoveFailed) {
			log.Printf("[news:delete] remove %q: %v", slug, err)
			http.Error(w, "failed to delete article", http.StatusInternalServerError)
			return
		}
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if !removed {
		// Nothing was there to begin with. That is a 404, not a server fault: the
		// admin UI retries on 5xx and shows "the server is broken", when in fact
		// the row it is trying to delete is simply gone already.
		http.Error(w, "article not found", http.StatusNotFound)
		return
	}
	// remove meta entry if exists
	m := readMeta(d)
	delete(m, slug)
	// Same as in Save: an unreported metadata failure leaves the deleted slug in
	// news_meta.json and the next rebuild puts it back into the index.
	if err := writeMeta(d, m); err != nil {
		log.Printf("[news:delete] write meta: %v", err)
		http.Error(w, "deleted but metadata update failed", http.StatusInternalServerError)
		return
	}
	if err := RebuildIndex(d); err != nil {
		log.Printf("[news:delete] rebuild index: %v", err)
		http.Error(w, "deleted but index rebuild failed", http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// Publish toggles the published flag in meta and rebuilds the index.
func (h *Handlers) Publish(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	scope := r.FormValue("scope")
	gid := r.FormValue("gameId")
	slug := r.FormValue("slug")
	pub := formBool(r.FormValue("published"))
	if strings.TrimSpace(slug) == "" {
		http.Error(w, "missing slug", http.StatusBadRequest)
		return
	}
	if !adminutil.IsSafeNewsSlug(slug) {
		http.Error(w, "invalid slug", http.StatusBadRequest)
		return
	}
	d, err := h.dirs(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	// Publishing a slug that has no markdown anywhere used to create a metadata
	// entry no file corresponds to. RebuildIndex skips it (it iterates over the
	// .md files), so the entry stayed in news_meta.json forever, invisible and
	// impossible to clear from the admin panel.
	if _, _, err := findArticle(d, slug); err != nil {
		http.Error(w, "article not found", http.StatusNotFound)
		return
	}
	m := readMeta(d)
	cur := m[slug]
	cur.Published = pub
	m[slug] = cur
	if err := writeMeta(d, m); err != nil {
		log.Printf("[news:publish] write meta: %v", err)
		http.Error(w, "failed to update metadata", http.StatusInternalServerError)
		return
	}
	// RebuildIndex moves the markdown between the private and the public tree to
	// match the new flag.
	if err := RebuildIndex(d); err != nil {
		log.Printf("[news:publish] rebuild index: %v", err)
		http.Error(w, "updated but index rebuild failed", http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]any{"status": "ok", "slug": slug, "published": pub})
}

// Rebuild regenerates index.json for the requested scope.
func (h *Handlers) Rebuild(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	scope := r.URL.Query().Get("scope")
	gid := r.URL.Query().Get("gameId")
	d, err := h.dirs(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if err := RebuildIndex(d); err != nil {
		log.Printf("[news:rebuild] %v", err)
		http.Error(w, "index rebuild failed", http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// Preview renders a card and the article HTML for the admin editor.
func (h *Handlers) Preview(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	// Same bound as Save: the editor previews the body it is about to save.
	r.Body = http.MaxBytesReader(w, r.Body, maxArticleBytes)
	// #nosec G120 -- the body is bounded by the MaxBytesReader above.
	if err := r.ParseMultipartForm(4 << 20); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	md := r.FormValue("markdown")
	// Build a compact "card" preview similar to client list: cover (first image), title, summary
	title, summary, cover := ExtractMeta(md)
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
	adminutil.WriteJSON(w, map[string]string{"listHtml": card, "contentHtml": contentHTML})
}

// UploadCover saves an uploaded image into content/news/assets and returns its
// coverUrl, optionally recording it in the metadata of a slug.
func (h *Handlers) UploadCover(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	r.Body = http.MaxBytesReader(w, r.Body, MaxImageBytes+(1<<20))
	// #nosec G120 -- the body is bounded by the MaxBytesReader above.
	if err := r.ParseMultipartForm(imageFormMemory); err != nil {
		http.Error(w, "request too large or malformed", http.StatusBadRequest)
		return
	}
	// Everything that can reject the request is checked before anything is
	// written: the image used to be stored (under a sanitised name, so no
	// traversal, but still) and only then did the slug check fire, so every
	// rejected upload left one more orphaned file in the gallery.
	scope := r.FormValue("scope")
	gid := r.FormValue("gameId")
	slug := strings.TrimSpace(r.FormValue("slug"))
	if slug != "" && !adminutil.IsSafeNewsSlug(slug) {
		http.Error(w, "invalid slug", http.StatusBadRequest)
		return
	}
	file, hdr, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file: "+err.Error(), http.StatusBadRequest)
		return
	}
	defer func() { _ = file.Close() }()
	base := h.assetsRoot()
	// #nosec G301 -- the gallery is handed out under /assets/ by nginx, which
	// runs as a different user than the API; 0750 would make it unreadable.
	if err := os.MkdirAll(base, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	name := adminutil.SanitizeFilename(hdr.Filename)
	outPath := filepath.Join(base, name)
	// Read through a limit reader: the request cap above bounds the body, but
	// the part itself must not be trusted to stay inside it. Buffering first and
	// writing atomically means a rejected or interrupted upload never replaces
	// the cover that is already published under this name.
	buf, err := io.ReadAll(io.LimitReader(file, MaxImageBytes+1))
	if err != nil {
		log.Printf("[news:cover] read upload: %v", err)
		http.Error(w, "failed to store cover", http.StatusInternalServerError)
		return
	}
	if len(buf) > MaxImageBytes {
		http.Error(w, "image too large", http.StatusRequestEntityTooLarge)
		return
	}
	if err := adminutil.WriteFileAtomic(outPath, buf, 0o644); err != nil {
		log.Printf("[news:cover] write %q: %v", outPath, err)
		http.Error(w, "failed to store cover", http.StatusInternalServerError)
		return
	}
	// Return a web path expected by the client/launcher
	coverURL := "/assets/" + name
	// Optionally update meta if scope+slug provided (already validated above).
	if d, derr := h.dirs(scope, gid); slug != "" && derr == nil {
		h.mu.Lock()
		defer h.mu.Unlock()
		if msg, err := h.attachCover(d, slug, coverURL); err != nil {
			log.Printf("[news:cover] %s: %v", msg, err)
			http.Error(w, "cover stored but "+msg, http.StatusInternalServerError)
			return
		}
	}
	adminutil.WriteJSON(w, map[string]string{"coverUrl": coverURL})
}

// attachCover records coverURL as the cover of slug and rebuilds the index. The
// image is stored either way, but if the metadata write fails the article keeps
// its old cover — reporting success would hide a change that did not happen.
// The returned string names the step that failed, for the log and the response.
func (h *Handlers) attachCover(d dirs, slug, coverURL string) (string, error) {
	m := readMeta(d)
	cur := m[slug]
	cur.CoverURL = coverURL
	m[slug] = cur
	if err := writeMeta(d, m); err != nil {
		return "metadata update failed", err
	}
	if err := RebuildIndex(d); err != nil {
		return "index rebuild failed", err
	}
	return "", nil
}

// newsItem is one entry of index.json. The field names are the wire contract
// with both the launcher and the admin UI.
type newsItem struct {
	ID        string `json:"id"`
	Title     string `json:"title"`
	Slug      string `json:"slug"`
	CreatedAt string `json:"createdAt"`
	Summary   string `json:"summary"`
	CoverURL  string `json:"coverUrl"`
	Published bool   `json:"published"`
}

// markdownSlugs lists the article slugs whose .md file sits in dir.
func markdownSlugs(dir string) []string {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	out := make([]string, 0, len(entries))
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(strings.ToLower(e.Name()), ".md") {
			continue
		}
		out = append(out, strings.TrimSuffix(e.Name(), ".md"))
	}
	return out
}

// RebuildIndex regenerates both indexes of a scope and, along the way, puts
// every article in the directory its published flag calls for.
//
// Two files are written because the two audiences differ:
//   - d.pub/index.json  — published articles only; nginx serves this directory
//     wholesale, so it must not even hint that a draft exists.
//   - d.priv/index.json — everything, for the admin UI (news.List).
//
// Articles found on the wrong side (drafts left in the served tree by an older
// build, or an article whose flag just changed) are moved here, which doubles
// as the migration path for content published before this split.
func RebuildIndex(d dirs) error {
	// #nosec G301 -- the content root is one tree managed by the deploy; see the
	// note on moveFile.
	if err := os.MkdirAll(d.priv, 0o755); err != nil {
		return err
	}
	items, err := collectItems(d, readMeta(d))
	if err != nil {
		return err
	}
	sort.Slice(items, func(i, j int) bool { return items[i].CreatedAt > items[j].CreatedAt })

	published := make([]newsItem, 0, len(items))
	for _, it := range items {
		if it.Published {
			published = append(published, it)
		}
	}
	if err := writeIndex(adminIndexPath(d), items); err != nil {
		return err
	}
	// #nosec G301 -- content/news is handed out by nginx; see moveFile.
	if err := os.MkdirAll(d.pub, 0o755); err != nil {
		return err
	}
	if err := writeIndex(publicIndexPath(d), published); err != nil {
		return err
	}
	// Any legacy metadata copy in the served tree is a leak of the draft list.
	_ = os.Remove(filepath.Join(d.pub, "news_meta.json"))
	// do not mutate meta during rebuild
	return nil
}

// locateArticle is findArticle with "not there" reduced to a plain bool: the
// rebuild simply skips a slug whose markdown it cannot find.
func locateArticle(d dirs, slug string) (string, bool, bool) {
	p, atPub, err := findArticle(d, slug)
	return p, atPub, err == nil
}

// readArticleBody returns the markdown stored at p, which findArticle resolved,
// or false when it cannot be read.
func readArticleBody(p string) (string, bool) {
	// #nosec G304 -- p comes from findArticle, i.e. from NewsSlugPath.
	b, err := os.ReadFile(p)
	return string(b), err == nil
}

// knownSlugs is the union of both directories, in public-then-private order.
// The public one may still hold drafts written by an older build.
func knownSlugs(d dirs) []string {
	seen := map[string]bool{}
	slugs := make([]string, 0)
	for _, dir := range []string{d.pub, d.priv} {
		for _, s := range markdownSlugs(dir) {
			if !seen[s] {
				seen[s] = true
				slugs = append(slugs, s)
			}
		}
	}
	return slugs
}

// collectItems builds the index entry of every article in the scope, putting
// each markdown on the side its published flag calls for along the way.
//
// A slug whose file cannot be found or read is left out of the index; a move
// that fails is an error, because dropping the article silently would take it
// off the launcher's list without anybody asking for that.
func collectItems(d dirs, meta map[string]meta) ([]newsItem, error) {
	var items []newsItem
	for _, slug := range knownSlugs(d) {
		if !adminutil.IsSafeNewsSlug(slug) {
			continue
		}
		src, ok, err := placeArticle(d, slug, meta[slug].Published)
		if err != nil {
			return nil, err
		}
		if !ok {
			continue
		}
		body, ok := readArticleBody(src)
		if !ok {
			continue
		}
		items = append(items, indexItem(slug, src, body, meta))
	}
	return items, nil
}

// placeArticle moves the markdown of slug to the directory pubWanted calls for
// and reports where it ended up. ok is false when the slug has no file at all.
func placeArticle(d dirs, slug string, pubWanted bool) (string, bool, error) {
	src, atPub, ok := locateArticle(d, slug)
	if !ok {
		return "", false, nil
	}
	if atPub != pubWanted {
		if dst, derr := articlePath(d, slug, pubWanted); derr == nil {
			if err := moveFile(src, dst); err != nil {
				return "", false, err
			}
			src = dst
		}
	}
	return src, true, nil
}

// indexItem builds the index entry of one article.
func indexItem(slug, path, body string, meta map[string]meta) newsItem {
	// compute content-based fields
	title, summary, coverFromBody := ExtractMeta(body)
	// Take cover and published strictly from the meta file; do not infer on
	// rebuild. Without an entry the index still has to stay consistent, so the
	// body's first image stands in and the article counts as a draft.
	cover, published := coverFromBody, false
	if entry, ok := meta[slug]; ok {
		cover, published = entry.CoverURL, entry.Published
	}
	created := time.Now().UTC().Format(time.RFC3339)
	// #nosec G703 -- path comes from findArticle, i.e. from NewsSlugPath.
	if st, err := os.Stat(path); err == nil {
		created = st.ModTime().UTC().Format(time.RFC3339)
	}
	return newsItem{
		ID:        slug,
		Title:     title,
		Slug:      slug,
		CreatedAt: created,
		Summary:   summary,
		CoverURL:  cover,
		Published: published,
	}
}

// writeIndex serialises an index file.
func writeIndex(path string, items []newsItem) error {
	if items == nil {
		items = []newsItem{}
	}
	b, err := json.MarshalIndent(struct {
		Items []newsItem `json:"items"`
	}{Items: items}, "", "  ")
	if err != nil {
		return err
	}
	// index.json is served to the launcher straight off disk; half of it is
	// worse than the previous generation of it.
	return adminutil.WriteFileAtomic(path, b, 0o644)
}

// assetURL builds the web path of an asset stored at rel/name.
func assetURL(rel, name string) string {
	return "/assets/" + strings.TrimPrefix(filepath.ToSlash(filepath.Join(rel, name)), "/")
}

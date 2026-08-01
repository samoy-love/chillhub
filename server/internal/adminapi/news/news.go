// Package news manages the markdown articles served to the launcher, their
// index.json, the per-article metadata file and the shared asset gallery.
//
// Every path built from request input goes through the guards in adminutil:
// scope/gameId via Base, slugs via adminutil.NewsSlugPath.
package news

import (
	"encoding/json"
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
			return "", fmt.Errorf("gameId required for scope=game")
		}
		if !adminutil.IsSafeGameID(gid) {
			return "", fmt.Errorf("invalid gameId")
		}
		p := filepath.Join(root, "games", gid)
		if !adminutil.EnsureWithin(root, p) {
			return "", fmt.Errorf("invalid gameId")
		}
		return p, nil
	}
	return "", fmt.Errorf("invalid scope: %s", scope)
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
			return dirs{}, fmt.Errorf("invalid gameId")
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
func findArticle(d dirs, slug string) (string, bool, error) {
	pubPath, err := adminutil.NewsSlugPath(d.pub, slug)
	if err != nil {
		return "", false, err
	}
	if _, err := os.Stat(pubPath); err == nil {
		return pubPath, true, nil
	}
	privPath, err := adminutil.NewsSlugPath(d.priv, slug)
	if err != nil {
		return "", false, err
	}
	if _, err := os.Stat(privPath); err == nil {
		return privPath, false, nil
	}
	return "", false, os.ErrNotExist
}

// moveFile relocates src onto dst, replacing dst if it exists (os.Rename
// refuses to overwrite on Windows).
func moveFile(src, dst string) error {
	if src == dst {
		return nil
	}
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	if err := os.Rename(src, dst); err == nil {
		return nil
	}
	b, err := os.ReadFile(src)
	if err != nil {
		return err
	}
	if err := os.WriteFile(dst, b, 0o644); err != nil {
		return err
	}
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
	w.Write(b)
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
	b, err := os.ReadFile(p)
	if err != nil {
		http.Error(w, "article not found", http.StatusNotFound)
		return
	}
	meta := readMeta(d)[slug]
	w.Header().Set("Content-Type", "application/json")
	adminutil.WriteJSON(w, map[string]any{"markdown": string(b), "published": meta.Published, "coverUrl": meta.CoverUrl})
}

// Save writes the markdown for a slug, updates metadata and rebuilds the index.
func (h *Handlers) Save(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
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
		cur.CoverUrl = cov
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
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		log.Printf("[news:save] mkdir %s: %v", filepath.Dir(p), err)
		http.Error(w, "failed to store article", http.StatusInternalServerError)
		return
	}
	if err := os.WriteFile(p, []byte(md), 0o644); err != nil {
		log.Printf("[news:save] write %s: %v", p, err)
		http.Error(w, "failed to store article", http.StatusInternalServerError)
		return
	}
	// Drop the copy in the other directory, if the article moved.
	if other, err := articlePath(d, slug, !cur.Published); err == nil {
		_ = os.Remove(other)
	}
	_ = writeMeta(d, m)
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
	// The article may sit in either directory, so remove both candidates and
	// report 500 only when a file that does exist refuses to go away.
	removed := false
	for _, published := range []bool{true, false} {
		p, perr := articlePath(d, slug, published)
		if perr != nil {
			http.Error(w, perr.Error(), http.StatusBadRequest)
			return
		}
		if err := os.Remove(p); err == nil {
			removed = true
		} else if !os.IsNotExist(err) {
			log.Printf("[news:delete] remove %s: %v", p, err)
			http.Error(w, "failed to delete article", http.StatusInternalServerError)
			return
		}
	}
	if !removed {
		http.Error(w, "article not found", http.StatusInternalServerError)
		return
	}
	// remove meta entry if exists
	m := readMeta(d)
	delete(m, slug)
	_ = writeMeta(d, m)
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
	pubStr := strings.TrimSpace(strings.ToLower(r.FormValue("published")))
	pub := pubStr == "true" || pubStr == "1" || pubStr == "yes"
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
	if err := r.ParseMultipartForm(imageFormMemory); err != nil {
		http.Error(w, "request too large or malformed", http.StatusBadRequest)
		return
	}
	file, hdr, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file: "+err.Error(), http.StatusBadRequest)
		return
	}
	defer file.Close()
	base := h.assetsRoot()
	if err := os.MkdirAll(base, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	name := adminutil.SanitizeFilename(hdr.Filename)
	outPath := filepath.Join(base, name)
	out, err := os.Create(outPath)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	// Copy through a limit reader: the request cap above bounds the body, but the
	// part itself must not be trusted to stay inside it.
	n, err := io.Copy(out, io.LimitReader(file, MaxImageBytes+1))
	if err != nil {
		out.Close()
		_ = os.Remove(outPath)
		log.Printf("[news:cover] write %s: %v", outPath, err)
		http.Error(w, "failed to store cover", http.StatusInternalServerError)
		return
	}
	out.Close()
	if n > MaxImageBytes {
		_ = os.Remove(outPath)
		http.Error(w, "image too large", http.StatusRequestEntityTooLarge)
		return
	}
	// Return a web path expected by the client/launcher
	url := "/assets/" + name
	// Optionally update meta if scope+slug provided
	scope := r.FormValue("scope")
	gid := r.FormValue("gameId")
	slug := r.FormValue("slug")
	if strings.TrimSpace(slug) != "" {
		if !adminutil.IsSafeNewsSlug(slug) {
			http.Error(w, "invalid slug", http.StatusBadRequest)
			return
		}
		if d, err := h.dirs(scope, gid); err == nil {
			h.mu.Lock()
			defer h.mu.Unlock()
			m := readMeta(d)
			cur := m[slug]
			cur.CoverUrl = url
			m[slug] = cur
			_ = writeMeta(d, m)
			_ = RebuildIndex(d)
		}
	}
	adminutil.WriteJSON(w, map[string]string{"coverUrl": url})
}

// newsItem is one entry of index.json. The field names are the wire contract
// with both the launcher and the admin UI.
type newsItem struct {
	Id        string `json:"id"`
	Title     string `json:"title"`
	Slug      string `json:"slug"`
	CreatedAt string `json:"createdAt"`
	Summary   string `json:"summary"`
	CoverUrl  string `json:"coverUrl"`
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
	if err := os.MkdirAll(d.priv, 0o755); err != nil {
		return err
	}
	meta := readMeta(d)

	// Union of both directories; the public one may still hold drafts.
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

	var items []newsItem
	for _, slug := range slugs {
		if !adminutil.IsSafeNewsSlug(slug) {
			continue
		}
		pubWanted := meta[slug].Published
		src, atPub, err := findArticle(d, slug)
		if err != nil {
			continue
		}
		if atPub != pubWanted {
			dst, derr := articlePath(d, slug, pubWanted)
			if derr == nil {
				if err := moveFile(src, dst); err != nil {
					return err
				}
				src = dst
			}
		}
		p := src
		b, err := os.ReadFile(p)
		if err != nil {
			continue
		}
		body := string(b)
		// compute content-based fields
		t, s, cFromBody := ExtractMeta(body)
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
		items = append(items, newsItem{
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

	published := make([]newsItem, 0, len(items))
	for _, it := range items {
		if it.Published {
			published = append(published, it)
		}
	}
	if err := writeIndex(adminIndexPath(d), items); err != nil {
		return err
	}
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

// writeIndex serialises an index file.
func writeIndex(path string, items []newsItem) error {
	if items == nil {
		items = []newsItem{}
	}
	b, _ := json.MarshalIndent(struct {
		Items []newsItem `json:"items"`
	}{Items: items}, "", "  ")
	// index.json is served to the launcher straight off disk; half of it is
	// worse than the previous generation of it.
	return adminutil.WriteFileAtomic(path, b, 0o644)
}

// assetURL builds the web path of an asset stored at rel/name.
func assetURL(rel, name string) string {
	return "/assets/" + strings.TrimPrefix(filepath.ToSlash(filepath.Join(rel, name)), "/")
}

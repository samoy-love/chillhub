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
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"ChillHub/server/internal/adminutil"
)

// Handlers serves the news endpoints for one content root.
type Handlers struct {
	root string
}

// New returns handlers rooted at the given content directory.
func New(root string) *Handlers { return &Handlers{root: root} }

// newsRoot is the directory every news path must stay inside.
func (h *Handlers) newsRoot() string { return filepath.Join(h.root, "news") }

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

// List returns index.json for the requested scope.
func (h *Handlers) List(w http.ResponseWriter, r *http.Request) {
	scope := r.URL.Query().Get("scope")
	gid := r.URL.Query().Get("gameId")
	base, err := h.Base(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	idxPath := filepath.Join(base, "index.json")
	b, err := os.ReadFile(idxPath)
	if err != nil {
		http.Error(w, err.Error(), http.StatusNotFound)
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
	base, err := h.Base(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	p, err := adminutil.NewsSlugPath(base, slug)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	b, err := os.ReadFile(p)
	if err != nil {
		http.Error(w, err.Error(), http.StatusNotFound)
		return
	}
	meta := readMeta(base)[slug]
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
	base, err := h.Base(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	p, err := adminutil.NewsSlugPath(base, slug)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if err := os.MkdirAll(base, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	if err := os.WriteFile(p, []byte(md), 0o644); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	// update meta if provided
	m := readMeta(base)
	cur := m[slug]
	if cov != "" {
		cur.CoverUrl = cov
	}
	// honor explicit published flag in save (if not provided, leave as-is)
	if r.Form.Has("published") {
		cur.Published = pub
	}
	m[slug] = cur
	_ = writeMeta(base, m)
	if err := RebuildIndex(base); err != nil {
		http.Error(w, "saved but index rebuild failed: "+err.Error(), http.StatusInternalServerError)
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
	base, err := h.Base(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	p, err := adminutil.NewsSlugPath(base, slug)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if err := os.Remove(p); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	// remove meta entry if exists
	m := readMeta(base)
	delete(m, slug)
	_ = writeMeta(base, m)
	if err := RebuildIndex(base); err != nil {
		http.Error(w, "deleted but index rebuild failed: "+err.Error(), http.StatusInternalServerError)
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
	base, err := h.Base(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	m := readMeta(base)
	cur := m[slug]
	cur.Published = pub
	m[slug] = cur
	if err := writeMeta(base, m); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	if err := RebuildIndex(base); err != nil {
		http.Error(w, "updated but index rebuild failed: "+err.Error(), http.StatusInternalServerError)
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
	base, err := h.Base(scope, gid)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	if err := RebuildIndex(base); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
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
	if err := r.ParseMultipartForm(32 << 20); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
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
	if _, err := io.Copy(out, file); err != nil {
		out.Close()
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	out.Close()
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
		if baseDir, err := h.Base(scope, gid); err == nil {
			m := readMeta(baseDir)
			cur := m[slug]
			cur.CoverUrl = url
			m[slug] = cur
			_ = writeMeta(baseDir, m)
			_ = RebuildIndex(baseDir)
		}
	}
	adminutil.WriteJSON(w, map[string]string{"coverUrl": url})
}

// RebuildIndex regenerates index.json from the markdown files in base.
func RebuildIndex(base string) error {
	entries, err := os.ReadDir(base)
	if err != nil {
		return err
	}
	meta := readMeta(base)
	type item struct {
		Id        string `json:"id"`
		Title     string `json:"title"`
		Slug      string `json:"slug"`
		CreatedAt string `json:"createdAt"`
		Summary   string `json:"summary"`
		CoverUrl  string `json:"coverUrl"`
		Published bool   `json:"published"`
	}
	var items []item
	for _, e := range entries {
		name := e.Name()
		if !strings.HasSuffix(strings.ToLower(name), ".md") {
			continue
		}
		slug := strings.TrimSuffix(name, ".md")
		p := filepath.Join(base, name)
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
		items = append(items, item{
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
	out := struct {
		Items []item `json:"items"`
	}{Items: items}
	b, _ := json.MarshalIndent(out, "", "  ")
	if err := os.WriteFile(filepath.Join(base, "index.json"), b, 0o644); err != nil {
		return err
	}
	// do not mutate meta during rebuild
	return nil
}

// assetURL builds the web path of an asset stored at rel/name.
func assetURL(rel, name string) string {
	return "/assets/" + strings.TrimPrefix(filepath.ToSlash(filepath.Join(rel, name)), "/")
}

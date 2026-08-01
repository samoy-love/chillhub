// Package games serves the admin-managed game registry
// (manifests/_registry/games.json) and the per-game icon upload.
package games

import (
	"bytes"
	"encoding/json"
	"image"
	"image/jpeg"
	"image/png"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"ChillHub/server/internal/adminutil"
)

// Entry is one record of the games registry.
type Entry struct {
	GameID          string `json:"gameId"`
	Title           string `json:"title"`
	ExeRelativePath string `json:"exeRelativePath"`
	IconURL         string `json:"iconUrl"`
}

// Handlers serves the games endpoints for one content root.
type Handlers struct {
	root string
}

// New returns handlers rooted at the given content directory.
func New(root string) *Handlers { return &Handlers{root: root} }

// registryPath stores the registry separately from any game ID to avoid collisions.
func (h *Handlers) registryPath() string {
	return filepath.Join(h.root, "manifests", "_registry", "games.json")
}

// FromManifests scans manifests/* and builds an initial registry list.
func (h *Handlers) FromManifests() []Entry {
	base := filepath.Join(h.root, "manifests")
	entries, err := os.ReadDir(base)
	if err != nil {
		return []Entry{}
	}
	items := make([]Entry, 0)
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		gid := e.Name()
		name := strings.ToLower(gid)
		// Skip special/system folders that are not games
		if name == "repo" || name == "_registry" || name == "launcher" {
			continue
		}
		items = append(items, Entry{GameID: gid, Title: gid, ExeRelativePath: "", IconURL: ""})
	}
	sort.Slice(items, func(i, j int) bool { return items[i].GameID < items[j].GameID })
	return items
}

// Get returns the registry, autogenerating it from the manifests on first use.
func (h *Handlers) Get(w http.ResponseWriter, r *http.Request) {
	p := h.registryPath()
	if _, err := os.Stat(p); err != nil {
		// Autogenerate from manifests/{gameId}/ directories (exclude 'launcher')
		items := h.FromManifests()
		outDir := filepath.Dir(p)
		_ = os.MkdirAll(outDir, 0o755)
		b, _ := json.MarshalIndent(struct {
			Items []Entry `json:"items"`
		}{Items: items}, "", "  ")
		_ = os.WriteFile(p, b, 0o644)
		w.Header().Set("Content-Type", "application/json")
		w.Write(b)
		return
	}
	b, err := os.ReadFile(p)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// Save overwrites the registry with the posted list.
func (h *Handlers) Save(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	var payload struct {
		Items []Entry `json:"items"`
	}
	if err := json.NewDecoder(r.Body).Decode(&payload); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	outDir := filepath.Dir(h.registryPath())
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	b, _ := json.MarshalIndent(payload, "", "  ")
	if err := os.WriteFile(h.registryPath(), b, 0o644); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// Scan returns the registry list derived from the manifests directory without
// touching the stored registry.
func (h *Handlers) Scan(w http.ResponseWriter, r *http.Request) {
	adminutil.WriteJSON(w, struct {
		Items []Entry `json:"items"`
	}{Items: h.FromManifests()})
}

// IconUpload saves the uploaded image as manifests/{gameId}/icon.png and
// returns its URL.
func (h *Handlers) IconUpload(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseMultipartForm(16 << 20); err != nil { // 16MB
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	gid := strings.TrimSpace(r.FormValue("gameId"))
	if gid == "" {
		http.Error(w, "missing gameId", http.StatusBadRequest)
		return
	}
	file, _, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file: "+err.Error(), http.StatusBadRequest)
		return
	}
	defer file.Close()
	data, err := io.ReadAll(file)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	// Decode using stdlib; support PNG/JPEG. For unsupported formats return 400.
	img, format, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		// try explicit decoders
		if im, e2 := png.Decode(bytes.NewReader(data)); e2 == nil {
			img = im
			format = "png"
		} else if im, e3 := jpeg.Decode(bytes.NewReader(data)); e3 == nil {
			img = im
			format = "jpeg"
		} else {
			http.Error(w, "unsupported image format", http.StatusBadRequest)
			return
		}
	}
	_ = format // currently unused; always encode PNG
	// Ensure directory and save as PNG with fixed name icon.png
	dir := filepath.Join(h.root, "manifests", gid)
	if err := os.MkdirAll(dir, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	outPath := filepath.Join(dir, "icon.png")
	if !adminutil.EnsureWithin(filepath.Join(h.root, "manifests"), outPath) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	out, err := os.Create(outPath)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	defer out.Close()
	if err := png.Encode(out, img); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	url := "/manifests/" + gid + "/icon.png"
	adminutil.WriteJSON(w, map[string]string{"status": "ok", "url": url})
}

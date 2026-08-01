package news

import (
	"io"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"ChillHub/server/internal/adminapi/media"
	"ChillHub/server/internal/adminutil"
)

// AssetsList returns the contents of a directory under content/news/assets for
// the gallery browser.
func (h *Handlers) AssetsList(w http.ResponseWriter, r *http.Request) {
	base := h.assetsRoot()
	rel := adminutil.SanitizeAssetPath(r.URL.Query().Get("path"))
	dir := filepath.Join(base, rel)
	if !adminutil.EnsureWithin(base, dir) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		http.Error(w, err.Error(), http.StatusNotFound)
		return
	}
	q := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("q")))
	dirsOnly := r.URL.Query().Get("dirsOnly") == "1"
	type item struct {
		Name    string `json:"name"`
		URL     string `json:"url"`
		Size    int64  `json:"size"`
		ModTime string `json:"modTime"`
		IsDir   bool   `json:"isDir"`
	}
	out := struct {
		Path  string `json:"path"`
		Items []item `json:"items"`
	}{Path: filepath.ToSlash(rel), Items: []item{}}
	for _, e := range entries {
		if e.IsDir() {
			name := e.Name()
			if q != "" && !strings.Contains(strings.ToLower(name), q) {
				continue
			}
			out.Items = append(out.Items, item{Name: name, URL: "", Size: 0, ModTime: "", IsDir: true})
			continue
		}
		if dirsOnly {
			continue
		}
		name := e.Name()
		if q != "" && !strings.Contains(strings.ToLower(name), q) {
			continue
		}
		info, _ := e.Info()
		out.Items = append(out.Items, item{
			Name: name,
			URL:  assetURL(rel, name),
			Size: func() int64 {
				if info != nil {
					return info.Size()
				}
				return 0
			}(),
			ModTime: func() string {
				if info != nil {
					return info.ModTime().UTC().Format(time.RFC3339)
				}
				return ""
			}(),
			IsDir: false,
		})
	}
	// sort by modTime desc
	sort.Slice(out.Items, func(i, j int) bool {
		if out.Items[i].IsDir != out.Items[j].IsDir {
			return out.Items[i].IsDir && !out.Items[j].IsDir
		}
		return out.Items[i].ModTime > out.Items[j].ModTime
	})
	adminutil.WriteJSON(w, out)
}

// AssetsMkdir creates a directory in the gallery (POST path, name).
func (h *Handlers) AssetsMkdir(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := h.assetsRoot()
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	name := adminutil.SanitizeFilename(r.FormValue("name"))
	if name == "" {
		http.Error(w, "empty name", http.StatusBadRequest)
		return
	}
	dir := filepath.Join(base, rel, name)
	if !adminutil.EnsureWithin(base, dir) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// AssetsUpload accepts a multipart image, converts it and stores it.
func (h *Handlers) AssetsUpload(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseMultipartForm(64 << 20); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := h.assetsRoot()
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	desired := adminutil.SanitizeFilename(r.FormValue("filename"))
	f, hdr, err := r.FormFile("file")
	if err != nil {
		http.Error(w, "missing file", http.StatusBadRequest)
		return
	}
	defer f.Close()
	data, err := io.ReadAll(f)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	inName := strings.ToLower(hdr.Filename)
	extHint := filepath.Ext(inName)
	if desired == "" {
		desired = strings.TrimSuffix(adminutil.SanitizeFilename(hdr.Filename), extHint)
	} else {
		// strip extension if client provided it
		desired = strings.TrimSuffix(desired, filepath.Ext(desired))
	}
	outName, metaFields, err := media.ProcessAndSaveAsset(base, rel, desired, data, extHint, "")
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	resp := map[string]string{"status": "ok", "url": assetURL(rel, outName), "filename": outName}
	for k, v := range metaFields {
		resp[k] = v
	}
	adminutil.WriteJSON(w, resp)
}

// AssetsUploadByURL downloads an image and processes it like a file upload.
func (h *Handlers) AssetsUploadByURL(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := h.assetsRoot()
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	desired := adminutil.SanitizeFilename(r.FormValue("filename"))
	// strip extension if provided by client; processor will add proper extension
	if desired != "" {
		desired = strings.TrimSuffix(desired, filepath.Ext(desired))
	}
	srcURL := strings.TrimSpace(r.FormValue("url"))
	if srcURL == "" {
		http.Error(w, "empty url", http.StatusBadRequest)
		return
	}
	data, ct, err := media.DownloadURL(srcURL)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	extHint := strings.ToLower(filepath.Ext(strings.Split(strings.Split(srcURL, "?")[0], "#")[0]))
	outName, metaFields, err := media.ProcessAndSaveAsset(base, rel, desired, data, extHint, ct)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	resp := map[string]string{"status": "ok", "url": assetURL(rel, outName), "filename": outName}
	for k, v := range metaFields {
		resp[k] = v
	}
	adminutil.WriteJSON(w, resp)
}

// AssetsDelete removes a file or directory from the gallery.
func (h *Handlers) AssetsDelete(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := h.assetsRoot()
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	name := adminutil.SanitizeFilename(r.FormValue("name"))
	if name == "" {
		http.Error(w, "empty name", http.StatusBadRequest)
		return
	}
	target := filepath.Join(base, rel, name)
	if !adminutil.EnsureWithin(base, target) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	if err := os.RemoveAll(target); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// AssetsRename renames a file or directory in the gallery.
func (h *Handlers) AssetsRename(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	base := h.assetsRoot()
	rel := adminutil.SanitizeAssetPath(r.FormValue("path"))
	from := adminutil.SanitizeFilename(r.FormValue("from"))
	to := adminutil.SanitizeFilename(r.FormValue("to"))
	if from == "" || to == "" {
		http.Error(w, "empty names", http.StatusBadRequest)
		return
	}
	src := filepath.Join(base, rel, from)
	dst := filepath.Join(base, rel, to)
	if !adminutil.EnsureWithin(base, src) || !adminutil.EnsureWithin(base, dst) {
		http.Error(w, "invalid path", http.StatusBadRequest)
		return
	}
	if err := os.Rename(src, dst); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

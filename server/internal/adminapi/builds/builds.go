// Package builds publishes launcher and game releases: it accepts ZIP uploads
// (plain, streaming and chunked), extracts them, computes the file manifest and
// activates a version.
//
// Publication is atomic: every archive is extracted into a staging directory on
// the same volume and only promoted over the live version once the whole tree
// is on disk and hashed.
package builds

import (
	"encoding/json"
	"log"
	"net/http"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"ChillHub/server/internal/adminutil"
)

// Handlers serves the build/version endpoints for one content root.
type Handlers struct {
	root string
	// CurrentUser resolves the acting admin. The upload endpoints enforce auth
	// themselves because nginx bypasses auth_request for them. It may be nil,
	// in which case those endpoints reject every request.
	CurrentUser func(*http.Request) string
}

// New returns handlers rooted at the given content directory.
func New(root string) *Handlers { return &Handlers{root: root} }

// authorized reports whether the request carries a valid admin session.
func (h *Handlers) authorized(r *http.Request) bool {
	return h.CurrentUser != nil && h.CurrentUser(r) != ""
}

// manifest describes a published version.
type manifest struct {
	Version   string         `json:"version"`
	BuildID   string         `json:"buildId"`
	GameID    string         `json:"gameId"`
	CreatedAt string         `json:"createdAt"`
	Files     []manifestFile `json:"files"`
	EmptyDirs []string       `json:"emptyDirs"`
	Signature string         `json:"signature"`
}

type manifestFile struct {
	Path       string `json:"path"`
	Size       int64  `json:"size"`
	Blake3     string `json:"blake3"`
	Sha256     string `json:"sha256,omitempty"`
	Executable bool   `json:"executable"`
}

func ensureTrailingSlash(s string) string {
	if s == "" {
		return s
	}
	if !strings.HasSuffix(s, "/") {
		return s + "/"
	}
	return s
}

// isExecutable is a simple heuristic for Windows builds.
func isExecutable(rel string) bool {
	return strings.HasSuffix(strings.ToLower(rel), ".exe")
}

func (h *Handlers) manifestsDir(gid string) string {
	return filepath.Join(h.root, "manifests", gid)
}

// stageVersionDir creates a staging directory for a build next to its final
// location, so that the finished tree can be moved into place with a rename on
// the same volume. Returns the staging dir and the files root inside it.
func (h *Handlers) stageVersionDir(gid, ver string) (stageDir, filesRoot string, err error) {
	parent := filepath.Join(h.root, "content", gid)
	if err = os.MkdirAll(parent, 0o755); err != nil {
		return "", "", err
	}
	stageDir = filepath.Join(parent, ver+".tmp-"+adminutil.GenID())
	filesRoot = filepath.Join(stageDir, "files")
	if err = os.MkdirAll(filesRoot, 0o755); err != nil {
		return "", "", err
	}
	return stageDir, filesRoot, nil
}

// promoteVersionDir atomically replaces the published version directory with a
// fully extracted staging directory. os.Rename cannot overwrite an existing
// directory (on any OS, and notably on Windows), so the old one is removed
// first.
func promoteVersionDir(stageDir, finalDir string) error {
	if err := os.MkdirAll(filepath.Dir(finalDir), 0o755); err != nil {
		return err
	}
	if _, err := os.Stat(finalDir); err == nil {
		if err := os.RemoveAll(finalDir); err != nil {
			return err
		}
	}
	return os.Rename(stageDir, finalDir)
}

// LauncherGameID is the game id the launcher publishes itself under.
const LauncherGameID = "launcher"

// LauncherStateFiles are files that live in the launcher's installation
// directory but belong to the USER, not to the build: the updater is told to
// preserve them and never overwrites them.
//
// They must never appear in a launcher manifest. If they do, the launcher
// compares their hashes against the manifest, the updater refuses to rewrite
// them, the mismatch is unresolvable — and the launcher offers the same update
// forever. That is exactly what happened with versions 1.0.2, 1.0.3 and 1.1.7.
//
// Keep in sync with PreserveMatcher.DefaultRules in updater/UpdatePreserve.cs;
// the guard test updater/tests/ManifestPreserveCheck enforces the invariant.
var LauncherStateFiles = []string{"config.json", "launcher.version"}

// stripLauncherStateFiles drops user-state entries from a launcher manifest.
// For regular games the names are ordinary content and are left alone.
func stripLauncherStateFiles(gameID string, files []manifestFile) []manifestFile {
	if !strings.EqualFold(strings.TrimSpace(gameID), LauncherGameID) || len(files) == 0 {
		return files
	}
	out := files[:0:0]
	for _, f := range files {
		rel := strings.TrimLeft(strings.ReplaceAll(f.Path, "\\", "/"), "/")
		drop := false
		for _, bad := range LauncherStateFiles {
			if strings.EqualFold(rel, bad) {
				drop = true
				break
			}
		}
		if drop {
			log.Printf("[builds] manifest %s: dropping user-state file %q (see LauncherStateFiles)", gameID, f.Path)
			continue
		}
		out = append(out, f)
	}
	return out
}

// writeManifest signs the manifest, stores it for a version and optionally
// points latest.json at it. Returns the manifest path and the exact bytes
// written.
//
// Signing happens here rather than at the call sites so that no publication
// path can accidentally emit an unsigned manifest.
func (h *Handlers) writeManifest(m manifest, updateLatest bool) (string, []byte, error) {
	m.Files = stripLauncherStateFiles(m.GameID, m.Files)
	signManifest(&m)
	outDir := h.manifestsDir(m.GameID)
	if err := os.MkdirAll(outDir, 0o755); err != nil {
		return "", nil, err
	}
	outPath := filepath.Join(outDir, m.Version+".json")
	b, _ := json.MarshalIndent(m, "", "  ")
	// Atomic: the public API serves these files while they are being written.
	if err := adminutil.WriteFileAtomic(outPath, b, 0o644); err != nil {
		return "", nil, err
	}
	if updateLatest {
		bl, _ := json.MarshalIndent(map[string]string{"version": m.Version}, "", "  ")
		_ = adminutil.WriteFileAtomic(filepath.Join(outDir, "latest.json"), bl, 0o644)
	}
	return outPath, b, nil
}

// ListVersions returns the versions available for a game plus the active one.
func (h *Handlers) ListVersions(w http.ResponseWriter, r *http.Request) {
	gid := r.URL.Query().Get("gameId")
	if gid == "" {
		http.Error(w, "missing gameId", http.StatusBadRequest)
		return
	}
	if !adminutil.IsSafeGameID(gid) {
		http.Error(w, "invalid gameId", http.StatusBadRequest)
		return
	}
	dir := h.manifestsDir(gid)
	if st, err := os.Stat(dir); err != nil || !st.IsDir() {
		dir = filepath.Join(h.root, "content", "manifests", gid)
	}
	entries, err := os.ReadDir(dir)
	if err != nil {
		// Do not echo the filesystem error: it carries the absolute content root.
		log.Printf("[builds] list %s: %v", gid, err)
		http.Error(w, "game not found", http.StatusNotFound)
		return
	}
	type item struct {
		Version string `json:"version"`
	}
	out := struct {
		Items  []item `json:"items"`
		Latest string `json:"latest"`
	}{Items: []item{}, Latest: ""}
	for _, e := range entries {
		name := e.Name()
		if strings.EqualFold(name, "latest.json") {
			continue
		}
		if strings.HasSuffix(strings.ToLower(name), ".json") {
			out.Items = append(out.Items, item{Version: strings.TrimSuffix(name, ".json")})
		}
	}
	sort.Slice(out.Items, func(i, j int) bool { return out.Items[i].Version < out.Items[j].Version })
	// read latest.json if present
	lb, err := os.ReadFile(filepath.Join(dir, "latest.json"))
	if err == nil {
		var m map[string]string
		if json.Unmarshal(lb, &m) == nil {
			if v := strings.TrimSpace(m["version"]); v != "" {
				out.Latest = v
			}
		}
	}
	adminutil.WriteJSON(w, out)
}

// Activate points latest.json at an existing version.
func (h *Handlers) Activate(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	gid := r.URL.Query().Get("gameId")
	ver := r.URL.Query().Get("version")
	if gid == "" || ver == "" {
		http.Error(w, "missing gameId or version", http.StatusBadRequest)
		return
	}
	if !adminutil.IsSafeGameID(gid) || !adminutil.IsSafeVersion(ver) {
		http.Error(w, "invalid gameId or version", http.StatusBadRequest)
		return
	}
	dir := h.manifestsDir(gid)
	if _, err := os.Stat(filepath.Join(dir, ver+".json")); err != nil {
		dir = filepath.Join(h.root, "content", "manifests", gid)
		if _, err2 := os.Stat(filepath.Join(dir, ver+".json")); err2 != nil {
			http.Error(w, "version manifest not found", http.StatusNotFound)
			return
		}
	}
	latest := map[string]string{"version": ver}
	b, _ := json.MarshalIndent(latest, "", "  ")
	if err := adminutil.WriteFileAtomic(filepath.Join(dir, "latest.json"), b, 0o644); err != nil {
		log.Printf("[builds] activate %s/%s: %v", gid, ver, err)
		http.Error(w, "failed to activate version", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Write(b)
}

// DeleteVersion removes manifests/{gid}/{ver}.json and content/{gid}/{ver},
// recomputing latest.json when it pointed at the deleted version.
func (h *Handlers) DeleteVersion(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
	gid := r.URL.Query().Get("gameId")
	ver := r.URL.Query().Get("version")
	if gid == "" || ver == "" {
		http.Error(w, "missing gameId or version", http.StatusBadRequest)
		return
	}
	if !adminutil.IsSafeGameID(gid) || !adminutil.IsSafeVersion(ver) {
		http.Error(w, "invalid gameId or version", http.StatusBadRequest)
		return
	}
	// remove manifest file
	manDir := h.manifestsDir(gid)
	manPath := filepath.Join(manDir, ver+".json")
	if err := os.Remove(manPath); err != nil {
		if !os.IsNotExist(err) {
			log.Printf("[builds] delete %s/%s: %v", gid, ver, err)
			http.Error(w, "failed to delete version", http.StatusInternalServerError)
			return
		}
	}
	// remove extracted content folder
	filesDir := filepath.Join(h.root, "content", gid, ver)
	_ = os.RemoveAll(filesDir)
	// adjust latest.json if it pointed to deleted version
	latestPath := filepath.Join(manDir, "latest.json")
	needRecalc := false
	if b, err := os.ReadFile(latestPath); err == nil {
		var m map[string]string
		if json.Unmarshal(b, &m) == nil {
			if strings.TrimSpace(m["version"]) == ver {
				needRecalc = true
			}
		}
	}
	if needRecalc {
		entries, _ := os.ReadDir(manDir)
		vers := make([]string, 0)
		for _, e := range entries {
			name := e.Name()
			if strings.EqualFold(name, "latest.json") {
				continue
			}
			if strings.HasSuffix(strings.ToLower(name), ".json") {
				vers = append(vers, strings.TrimSuffix(name, ".json"))
			}
		}
		sort.Slice(vers, func(i, j int) bool { return vers[i] < vers[j] })
		if len(vers) == 0 {
			// no versions remain: remove latest.json
			_ = os.Remove(latestPath)
		} else {
			newLatest := vers[len(vers)-1]
			b, _ := json.MarshalIndent(map[string]string{"version": newLatest}, "", "  ")
			_ = adminutil.WriteFileAtomic(latestPath, b, 0o644)
		}
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// FreeSpace reports free and total bytes on the volume holding the content root.
func (h *Handlers) FreeSpace(w http.ResponseWriter, r *http.Request) {
	base := h.root
	if strings.TrimSpace(base) == "" {
		base = "."
	}
	// Prefer getting both free and total where supported
	var free, total uint64
	if f, t, err := diskSpaceImpl(base); err == nil {
		free, total = f, t
	} else if f2, err2 := freeSpaceBytes(base); err2 == nil {
		free = f2
		total = 0
	} else {
		log.Printf("[builds] free space %s: %v", base, err)
		http.Error(w, "failed to query free space", http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]any{
		"path":  base,
		"bytes": free,
		"total": total,
	})
}

// freeSpaceBytes returns available free bytes on the filesystem that contains
// the given path.
func freeSpaceBytes(path string) (uint64, error) {
	// Ensure path exists to resolve volume/root correctly
	base := path
	if base == "" {
		base = "."
	}
	if _, err := os.Stat(base); os.IsNotExist(err) {
		if err2 := os.MkdirAll(base, 0o755); err2 != nil {
			// fallback to its parent
			base = filepath.Dir(base)
		}
	}
	return freeSpaceBytesImpl(base)
}

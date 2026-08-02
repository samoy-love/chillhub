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
	"sync"

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

// promoteVersionDir replaces the published version directory with a fully
// extracted staging directory.
//
// os.Rename cannot overwrite an existing directory (on any OS, and notably on
// Windows), so the old version has to be moved out of the way first. It is
// RENAMED ASIDE rather than deleted:
//
//   - deleting it left the version absent for as long as the delete took —
//     minutes for a multi-gigabyte build — and every client asking for it in
//     that window got a 404; two renames close that gap to microseconds;
//   - if the second rename then failed, the published version was gone for
//     good with nothing to restore. Now it is put back.
//
// The old tree is deleted only once the new one is live.
func promoteVersionDir(stageDir, finalDir string) error {
	if err := os.MkdirAll(filepath.Dir(finalDir), 0o755); err != nil {
		return err
	}
	// Sweep backups a previous crash may have left next to this version.
	if leftovers, err := filepath.Glob(finalDir + ".old-*"); err == nil {
		for _, p := range leftovers {
			_ = os.RemoveAll(p)
		}
	}

	backup := ""
	if _, err := os.Stat(finalDir); err == nil {
		backup = finalDir + ".old-" + adminutil.GenID()
		if err := os.Rename(finalDir, backup); err != nil {
			return err
		}
	}
	if err := os.Rename(stageDir, finalDir); err != nil {
		if backup != "" {
			if rerr := os.Rename(backup, finalDir); rerr != nil {
				// Both the promote and the rollback failed: the published version
				// now only exists under the backup name. Say so loudly — it is
				// recoverable by hand, and silence would hide that.
				log.Printf("[builds] CRITICAL: promote of %s failed (%v) and rollback failed too (%v); "+
					"the previous version is still on disk as %s", finalDir, err, rerr, backup)
			}
		}
		return err
	}
	if backup != "" {
		if err := os.RemoveAll(backup); err != nil {
			log.Printf("[builds] cannot remove the replaced version %s: %v", backup, err)
		}
	}
	return nil
}

// publishLocks serialises publication per gameId+version.
//
// promoteVersionDir first deletes every finalDir+".old-*" it finds and only
// then renames the live version aside under that same pattern. Two publications
// of the SAME version running at once therefore destroy each other's backup,
// and — worse — the winner of the content rename is not necessarily the one
// that writes the manifest last, so the published manifest can describe the
// other build's files. Every publication path takes this lock around
// promote + writeManifest, which is short (two renames and a small JSON write)
// and does not serialise the long extraction.
var publishLocks struct {
	mu sync.Mutex
	m  map[string]*publishLock
}

type publishLock struct {
	mu   sync.Mutex
	refs int
}

// lockPublish blocks until the gameId+version pair is free and returns the
// unlock function. Entries are reference-counted so the map cannot grow with
// every published version.
func lockPublish(gid, ver string) func() {
	key := strings.ToLower(gid) + "\x00" + ver
	publishLocks.mu.Lock()
	if publishLocks.m == nil {
		publishLocks.m = make(map[string]*publishLock)
	}
	l := publishLocks.m[key]
	if l == nil {
		l = &publishLock{}
		publishLocks.m[key] = l
	}
	l.refs++
	publishLocks.mu.Unlock()

	l.mu.Lock()
	return func() {
		l.mu.Unlock()
		publishLocks.mu.Lock()
		l.refs--
		if l.refs == 0 {
			delete(publishLocks.m, key)
		}
		publishLocks.mu.Unlock()
	}
}

// LauncherGameID is the game id the launcher publishes itself under.
const LauncherGameID = "launcher"

// LauncherStateFiles are files that live in the launcher's installation
// directory but do NOT come from a build: user state, or files the installer
// itself puts there. The updater is told to preserve them and never overwrites
// them.
//
// They must never appear in a launcher manifest. If they do, the launcher
// compares their hashes against the manifest, the updater refuses to rewrite
// them, the mismatch is unresolvable — and the launcher offers the same update
// forever. That is exactly what happened with versions 1.0.2, 1.0.3 and 1.1.7.
//
// Uninstall.exe belongs here for the same reason: it is generated by the NSIS
// installer on the user's machine, so its bytes differ from whatever copy got
// swept into the build ZIP, and a freshly installed user was told to update
// again immediately, forever.
//
// The comparison is on the EXACT top-level path (see stripLauncherStateFiles),
// not a prefix or a basename match, so a file of the same name deeper in the
// tree is left alone.
//
// Keep in sync with PreserveMatcher.DefaultRules in updater/UpdatePreserve.cs;
// the guard test updater/tests/ManifestPreserveCheck enforces the invariant.
var LauncherStateFiles = []string{"config.json", "launcher.version", "launcher.update-status", "Uninstall.exe"}

// LauncherUpdaterArtifacts are files the update machinery itself writes into the
// installation directory. They are not part of any build.
//
// They belong in a launcher manifest even less than the preserve rules do: the
// launcher skips them in the integrity check, in the download plan and in the
// delete list, and the updater actively scrubs them. Publishing them therefore
// promises the client files it will never fetch and never verify — and the
// repository's own guard (updater/tests/ManifestPreserveCheck) already reports
// such a manifest as a violation, while the publishing side used to emit it
// happily.
//
// Keep in sync with PreserveMatcher.UpdaterArtifactFiles in
// updater/UpdatePreserve.cs.
var LauncherUpdaterArtifacts = []string{
	"filelist.txt", "emptydirs.txt", "deletelist.txt", "apply-update.log", "apply-update.cmd",
}

// LauncherUpdaterArtifactDir is the directory an older updater mirrored into the
// installation root. Everything under it is scrubbed by the client, so nothing
// under it may be published. Keep in sync with
// PreserveMatcher.UpdaterArtifactDir.
const LauncherUpdaterArtifactDir = "updater"

// isLauncherNonPayload reports whether a launcher-relative path names a file the
// client will never write from a manifest: user state, an installation-time
// artifact, or leftovers of the updater.
//
// The comparison is on the EXACT top-level path (plus the one directory prefix),
// which is what the client's PreserveMatcher does; a basename match here would
// silently drop legitimate content such as data/config.json.
func isLauncherNonPayload(rel string) bool {
	for _, bad := range LauncherStateFiles {
		if strings.EqualFold(rel, bad) {
			return true
		}
	}
	for _, bad := range LauncherUpdaterArtifacts {
		if strings.EqualFold(rel, bad) {
			return true
		}
	}
	return len(rel) > len(LauncherUpdaterArtifactDir) &&
		strings.EqualFold(rel[:len(LauncherUpdaterArtifactDir)+1], LauncherUpdaterArtifactDir+"/")
}

// stripLauncherStateFiles drops user-state entries from a launcher manifest.
// For regular games the names are ordinary content and are left alone.
func stripLauncherStateFiles(gameID string, files []manifestFile) []manifestFile {
	if !strings.EqualFold(strings.TrimSpace(gameID), LauncherGameID) || len(files) == 0 {
		return files
	}
	out := files[:0:0]
	for _, f := range files {
		rel := strings.TrimLeft(strings.ReplaceAll(f.Path, "\\", "/"), "/")
		if isLauncherNonPayload(rel) {
			log.Printf("[builds] manifest %s: dropping user-state file %q (see LauncherStateFiles)", gameID, f.Path)
			continue
		}
		out = append(out, f)
	}
	return out
}

// isLauncherNonPayloadDir reports whether a launcher-relative DIRECTORY is one
// the client will never keep.
//
// It is the file rule plus the artifact directory itself: the updater runs
// Directory.Delete(<install>/updater, recursive) in CleanupUpdaterArtifacts, so
// "updater" is removed as well as everything under it.
func isLauncherNonPayloadDir(rel string) bool {
	dir := strings.TrimRight(rel, "/")
	return isLauncherNonPayload(dir) || strings.EqualFold(dir, LauncherUpdaterArtifactDir)
}

// stripLauncherStateDirs drops the same paths from a launcher manifest's
// emptyDirs list.
//
// Directories carry no hashes, so a stray entry here cannot produce the endless
// update loop a stray FILE does — but it is the same broken promise, and the
// hole stayed open while only Files were filtered. The client creates every
// emptyDir it is given and the updater deletes the "updater" directory in the
// very same run, so the manifest permanently describes an installation that
// cannot exist.
func stripLauncherStateDirs(gameID string, dirs []string) []string {
	if !strings.EqualFold(strings.TrimSpace(gameID), LauncherGameID) || len(dirs) == 0 {
		return dirs
	}
	out := dirs[:0:0]
	for _, d := range dirs {
		rel := strings.TrimLeft(strings.ReplaceAll(d, "\\", "/"), "/")
		if isLauncherNonPayloadDir(rel) {
			log.Printf("[builds] manifest %s: dropping updater-owned directory %q (see LauncherUpdaterArtifactDir)", gameID, d)
			continue
		}
		out = append(out, d)
	}
	return out
}

// writeManifest validates the manifest, stores it for a version and optionally
// points latest.json at it. Returns the manifest path and the exact bytes
// written.
//
// Validation happens here rather than at the call sites so that no publication
// path can accidentally emit a manifest the client will refuse.
func (h *Handlers) writeManifest(m manifest, updateLatest bool) (string, []byte, error) {
	m.Files = stripLauncherStateFiles(m.GameID, m.Files)
	m.EmptyDirs = stripLauncherStateDirs(m.GameID, m.EmptyDirs)

	// Публиковать манифест, который клиент заведомо отвергнет, бессмысленно:
	// лучше сломать выкладку здесь, с внятной причиной, чем у пользователя на
	// установке. Правила те же, что и на клиенте (ManifestValidator).
	if err := validateManifest(m); err != nil {
		log.Printf("[builds] refusing to publish manifest gameId=%q version=%q: %v", m.GameID, m.Version, err)
		return "", nil, err
	}

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
	// Ascending, as the admin UI expects, but compared by numeric components:
	// a plain string sort puts 1.1.10 before 1.1.9.
	sort.SliceStable(out.Items, func(i, j int) bool {
		return adminutil.CompareVersions(out.Items[i].Version, out.Items[j].Version) < 0
	})
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
		if len(vers) == 0 {
			// no versions remain: remove latest.json
			_ = os.Remove(latestPath)
		} else {
			// Same trap as in ListVersions: the highest version is not the last
			// one in string order (1.1.9 > 1.1.10 lexicographically).
			newLatest := adminutil.MaxVersion(vers)
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
	// "path" is deliberately absent: it is the absolute content root, and the
	// panel only ever displays the numbers.
	adminutil.WriteJSON(w, map[string]any{
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

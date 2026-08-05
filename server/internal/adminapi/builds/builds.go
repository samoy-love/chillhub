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
	"fmt"
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

// Permissions for everything this package creates.
//
// The published tree is not read by this process alone: nginx serves content/
// and manifests/ straight off disk (deploy/launcher.conf), and the deployment
// and backup jobs walk the same directories. Narrowing these bits is an
// infrastructure change, not a code change, so the values are the historic ones
// — named here only so that a single place decides them. On the host they end
// up tighter anyway: chillhub-admin.service sets UMask=0027.
const (
	contentDirPerm  = 0o755
	contentFilePerm = 0o644
)

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
func (h *Handlers) stageVersionDir(gid, ver string) (string, string, error) {
	// gid and ver are proven safe by adminutil.IsSafeGameID/IsSafeVersion at
	// every entry point before publication starts; neither can contain a
	// separator or a "..".
	parent := filepath.Join(h.root, "content", gid)
	if err := os.MkdirAll(parent, contentDirPerm); err != nil {
		return "", "", err
	}
	stageDir := filepath.Join(parent, ver+".tmp-"+adminutil.GenID())
	filesRoot := filepath.Join(stageDir, "files")
	if err := os.MkdirAll(filesRoot, contentDirPerm); err != nil {
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
	if err := os.MkdirAll(filepath.Dir(finalDir), contentDirPerm); err != nil {
		return err
	}
	sweepStaleBackups(finalDir)

	backup, err := moveLiveVersionAside(finalDir)
	if err != nil {
		return err
	}
	if err := os.Rename(stageDir, finalDir); err != nil {
		rollbackLiveVersion(backup, finalDir, err)
		return err
	}
	dropBackup(backup)
	return nil
}

// sweepStaleBackups removes backups a previous crash may have left next to this
// version.
func sweepStaleBackups(finalDir string) {
	leftovers, err := filepath.Glob(finalDir + ".old-*")
	if err != nil {
		return
	}
	for _, p := range leftovers {
		_ = os.RemoveAll(p)
	}
}

// moveLiveVersionAside renames the published version out of the way and returns
// its new path, or "" when there was nothing published yet.
func moveLiveVersionAside(finalDir string) (string, error) {
	// A stat that fails for any reason means "nothing to move aside", exactly as
	// before: the rename below would fail on its own if the directory is really
	// there but unreachable.
	if _, statErr := os.Stat(finalDir); statErr != nil {
		return "", nil //nolint:nilerr // absence is the expected case, not an error to report
	}
	backup := finalDir + ".old-" + adminutil.GenID()
	if err := os.Rename(finalDir, backup); err != nil {
		return "", err
	}
	return backup, nil
}

// rollbackLiveVersion puts the previous version back after a failed promote.
func rollbackLiveVersion(backup, finalDir string, cause error) {
	if backup == "" {
		return
	}
	if rerr := os.Rename(backup, finalDir); rerr != nil {
		// Both the promote and the rollback failed: the published version now
		// only exists under the backup name. Say so loudly — it is recoverable
		// by hand, and silence would hide that.
		log.Printf("[builds] CRITICAL: promote of %s failed (%v) and rollback failed too (%v); "+
			"the previous version is still on disk as %s", finalDir, cause, rerr, backup)
	}
}

// dropBackup deletes the replaced version once the new one is live.
func dropBackup(backup string) {
	if backup == "" {
		return
	}
	if err := os.RemoveAll(backup); err != nil {
		log.Printf("[builds] cannot remove the replaced version %s: %v", backup, err)
	}
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
	if err := os.MkdirAll(outDir, contentDirPerm); err != nil {
		return "", nil, err
	}
	outPath := filepath.Join(outDir, m.Version+".json")
	b, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return "", nil, err
	}
	// Atomic: the public API serves these files while they are being written.
	if err := adminutil.WriteFileAtomic(outPath, b, contentFilePerm); err != nil {
		return "", nil, err
	}
	if updateLatest {
		// A failed latest.json write FAILS THE PUBLICATION.
		//
		// The operator ticked "update latest", so the whole point of the request
		// was to make this version the one launchers download. If the pointer is
		// not repointed, the version sits on disk and every client keeps getting
		// the previous one — while the panel says the build was published. The
		// operator walks away, and the discrepancy surfaces days later as
		// "players do not get the update".
		//
		// The files already written are harmless: an unreferenced version
		// directory and its manifest are exactly what an inactive version looks
		// like, and republishing overwrites them. Reporting the failure costs a
		// retry; hiding it costs a silent non-release.
		if err := writeLatestJSON(outDir, m.Version); err != nil {
			log.Printf("[builds] manifest %s/%s written but latest.json not updated: %v", m.GameID, m.Version, err)
			return "", nil, fmt.Errorf("manifest written but latest.json not updated: %w", err)
		}
	}
	return outPath, b, nil
}

// writeLatestJSON points the game's latest.json at a version.
func writeLatestJSON(dir, version string) error {
	b, err := json.MarshalIndent(map[string]string{"version": version}, "", "  ")
	if err != nil {
		return err
	}
	return adminutil.WriteFileAtomic(filepath.Join(dir, "latest.json"), b, contentFilePerm)
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
	for _, v := range manifestVersions(entries) {
		out.Items = append(out.Items, item{Version: v})
	}
	// Ascending, as the admin UI expects, but compared by numeric components:
	// a plain string sort puts 1.1.10 before 1.1.9.
	sort.SliceStable(out.Items, func(i, j int) bool {
		return adminutil.CompareVersions(out.Items[i].Version, out.Items[j].Version) < 0
	})
	out.Latest = readLatestVersion(dir)
	adminutil.WriteJSON(w, out)
}

// manifestVersions returns the versions named by the *.json files of a manifest
// directory, latest.json excluded.
func manifestVersions(entries []os.DirEntry) []string {
	vers := make([]string, 0, len(entries))
	for _, e := range entries {
		name := e.Name()
		if strings.EqualFold(name, "latest.json") {
			continue
		}
		if strings.HasSuffix(strings.ToLower(name), ".json") {
			vers = append(vers, strings.TrimSuffix(name, ".json"))
		}
	}
	return vers
}

// readLatestVersion returns the version latest.json points at, or "" when the
// file is absent or unreadable.
func readLatestVersion(dir string) string {
	lb, err := os.ReadFile(filepath.Join(dir, "latest.json"))
	if err != nil {
		return ""
	}
	var m map[string]string
	if json.Unmarshal(lb, &m) != nil {
		return ""
	}
	return strings.TrimSpace(m["version"])
}

// versionManifestsDir returns the manifest directory that actually holds
// {ver}.json for a game, and whether it was found there.
//
// A content root pointed at the parent directory (resolveContentRoot in
// cmd/api, adminutil.DetectContentRoot in cmd/admin) leaves the manifests under
// {root}/content/manifests/{gid} instead of {root}/manifests/{gid}. A handler
// that knows only the first layout works on the wrong directory in silence: the
// delete endpoint answered 200 while the manifest stayed, so the version kept
// showing in the list and stayed activatable although its files were already
// gone. Presence of the version file — not of the directory — is what tells the
// two layouts apart. When neither holds it, the primary path is returned so
// that callers which treat a missing version as "already done" keep doing so.
func (h *Handlers) versionManifestsDir(gid, ver string) (string, bool) {
	dir := h.manifestsDir(gid)
	if _, err := os.Stat(filepath.Join(dir, ver+".json")); err == nil {
		return dir, true
	}
	legacy := filepath.Join(h.root, "content", "manifests", gid)
	if _, err := os.Stat(filepath.Join(legacy, ver+".json")); err == nil {
		return legacy, true
	}
	return dir, false
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
	dir, ok := h.versionManifestsDir(gid, ver)
	if !ok {
		http.Error(w, "version manifest not found", http.StatusNotFound)
		return
	}
	b, err := json.MarshalIndent(map[string]string{"version": ver}, "", "  ")
	if err == nil {
		err = adminutil.WriteFileAtomic(filepath.Join(dir, "latest.json"), b, contentFilePerm)
	}
	if err != nil {
		log.Printf("[builds] activate %s/%s: %v", gid, ver, err)
		http.Error(w, "failed to activate version", http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	// The response body is the same JSON that was just persisted; a failed write
	// to the client is the client's problem and cannot be reported any more.
	_, _ = w.Write(b)
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
	manDir, _ := h.versionManifestsDir(gid, ver)
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
	if err := os.RemoveAll(filesDir); err != nil {
		// A locked file, a lost permission or a mount point under the version
		// directory leaves gigabytes on disk while the panel counts the version as
		// gone, and the free-space figure it shows drifts away from reality with
		// nothing anywhere to explain it. The journal gets the absolute path — it
		// is not public, unlike the response body.
		//
		// The status stays 200 on purpose: the manifest is already removed, so the
		// version has disappeared for every client, and the operation the operator
		// asked for is complete. A 500 would make the panel retry deleting a
		// version that is no longer in the list and turn a finished job into a
		// failure. What is left is a leftover directory for the operator to clear,
		// and the journal is where that belongs.
		log.Printf("[builds] delete content %s/%s: %v", gid, ver, err)
	}
	// adjust latest.json if it pointed to deleted version
	if readLatestVersion(manDir) == ver {
		recalcLatest(manDir)
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// recalcLatest repoints latest.json at the highest remaining version, or
// removes it when the game has none left.
func recalcLatest(manDir string) {
	entries, _ := os.ReadDir(manDir)
	vers := manifestVersions(entries)
	latestPath := filepath.Join(manDir, "latest.json")
	if len(vers) == 0 {
		// no versions remain: remove latest.json
		_ = os.Remove(latestPath)
		return
	}
	// Same trap as in ListVersions: the highest version is not the last one in
	// string order (1.1.9 > 1.1.10 lexicographically).
	if err := writeLatestJSON(manDir, adminutil.MaxVersion(vers)); err != nil {
		log.Printf("[builds] cannot repoint %s: %v", latestPath, err)
	}
}

// FreeSpace reports free and total bytes on the volume holding the content root.
func (h *Handlers) FreeSpace(w http.ResponseWriter, _ *http.Request) {
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
		// Both probes are reported. Logging only the first one left the operator
		// with "no such file or directory" from diskSpaceImpl and no word about
		// why the fallback — which creates the directory before measuring — also
		// gave up, and those two fail for different reasons.
		log.Printf("[builds] free space %s: %v (free-bytes probe: %v)", base, err, err2)
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
		if err2 := os.MkdirAll(base, contentDirPerm); err2 != nil {
			// fallback to its parent
			base = filepath.Dir(base)
		}
	}
	return freeSpaceBytesImpl(base)
}

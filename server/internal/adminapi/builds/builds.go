// Package builds publishes launcher and game releases: it accepts ZIP uploads
// (plain, streaming and chunked), extracts them, computes the file manifest and
// activates a version.
//
// Publication is atomic: every archive is extracted into a staging directory on
// the same volume and only promoted over the live version once the whole tree
// is on disk and hashed.
package builds

import (
	"context"
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
	return h.writeManifestTo(h.manifestsDir(m.GameID), m, updateLatest)
}

// writeManifestTo is writeManifest with the destination directory supplied.
//
// A game's own builds always land in manifests/{gameId}; modpacks land in
// manifests/_mods/{gameId}. The destination is the ONLY difference between the
// two, so it is a parameter rather than a second copy of the validation, the
// state-file stripping and the atomic write — the three things that must never
// diverge between publication paths.
func (h *Handlers) writeManifestTo(outDir string, m manifest, updateLatest bool) (string, []byte, error) {
	m.Files = stripLauncherStateFiles(m.GameID, m.Files)
	m.EmptyDirs = stripLauncherStateDirs(m.GameID, m.EmptyDirs)

	// Публиковать манифест, который клиент заведомо отвергнет, бессмысленно:
	// лучше сломать выкладку здесь, с внятной причиной, чем у пользователя на
	// установке. Правила те же, что и на клиенте (ManifestValidator).
	if err := validateManifest(m); err != nil {
		log.Printf("[builds] refusing to publish manifest gameId=%q version=%q: %v", m.GameID, m.Version, err)
		return "", nil, err
	}

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
	entries, err := os.ReadDir(dir)
	if err != nil {
		// Do not echo the filesystem error: it carries the absolute content root.
		log.Printf("[builds] list %s: %v", gid, err)
		http.Error(w, "game not found", http.StatusNotFound)
		return
	}
	// Версия, дата, размер и число файлов. Раньше отдавалась только версия, и
	// таблица в админке состояла из строк «1.3.5 — —»: что это за сборка,
	// когда собрана и сколько весит, узнать было неоткуда, а решение «можно ли
	// удалить» принимается именно по этим полям.
	type item struct {
		Version   string `json:"version"`
		CreatedAt string `json:"createdAt,omitempty"`
		Files     int    `json:"files"`
		Bytes     int64  `json:"bytes"`
	}
	out := struct {
		Items  []item `json:"items"`
		Latest string `json:"latest"`
	}{Items: []item{}, Latest: ""}
	for _, v := range manifestVersions(entries) {
		created, files, bytes := manifestStats(filepath.Join(dir, v+".json"))
		out.Items = append(out.Items, item{Version: v, CreatedAt: created, Files: files, Bytes: bytes})
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

// manifestStats reads one version manifest and reports when it was built, how
// many files it lists and their total size. A manifest that cannot be read or
// parsed yields zero values: the versions list is a convenience view, and a
// single broken file must not blank the whole table.
func manifestStats(path string) (createdAt string, files int, bytes int64) {
	b, err := os.ReadFile(path)
	if err != nil {
		return "", 0, 0
	}
	// Decoding into the narrow shape keeps the hashes (the bulk of the file)
	// out of memory twice over for every version in the list.
	var m struct {
		CreatedAt string `json:"createdAt"`
		Files     []struct {
			Size int64 `json:"size"`
		} `json:"files"`
	}
	if json.Unmarshal(b, &m) != nil {
		return "", 0, 0
	}
	for _, f := range m.Files {
		bytes += f.Size
	}
	return m.CreatedAt, len(m.Files), bytes
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

// hasVersionManifest reports whether {ver}.json exists for a game.
//
// There is exactly one layout: {root}/manifests/{gid}. An inherited one put the
// manifests under {root}/content/manifests/{gid} when the content root was
// pointed at the parent directory, and the handlers used to search both places.
// Nothing on the server has that layout — the admin service runs with
// CONTENT_ROOT=/var/www/launcher and there is no content/manifests under it —
// so the search was a second path through a destructive operation that could
// never fire. Should the inherited layout ever come back, the fix is to point
// CONTENT_ROOT at the directory that actually holds manifests/, not to teach
// every handler two ways of finding the same file.
func (h *Handlers) hasVersionManifest(gid, ver string) bool {
	_, err := os.Stat(filepath.Join(h.manifestsDir(gid), ver+".json"))
	return err == nil
}

// launcherVersionAlreadyPublished refuses to publish a LAUNCHER build under a
// version number that already has a manifest.
//
// promoteVersionDir happily replaces an existing version directory — by
// design, and correctly so for games, where a same-version re-upload is a
// legitimate "fix this build without a new number" workflow. For the
// launcher it is not: self-update compares version STRINGS, not content, so
// a client that already updated to "1.3.0" from a first upload has no way to
// notice a second upload silently replacing what "1.3.0" means. That is
// exactly what happened on 2026-08-08: the same version got re-uploaded
// three times in one day, and every client that had already "updated" to the
// first of the three never saw the other two.
//
// Scoped to gid=="launcher" only — games keep the existing overwrite
// behaviour, since nothing about that incident applies to them.
//
// This alone only tells a caller "a manifest already exists here" — it does
// not by itself distinguish the incident (different content, same number)
// from a harmless retry (the exact same content, re-published because an
// earlier attempt failed after promotion but before the deploy finished).
// See launcherRepublishMatches, which every call site but UploadInit uses to
// tell the two apart once the new bytes are actually on disk to compare.
func (h *Handlers) launcherVersionAlreadyPublished(gid, ver string) bool {
	return gid == "launcher" && h.hasVersionManifest(gid, ver)
}

// launcherVersionConflictMessage is the operator-facing explanation for
// launcherVersionAlreadyPublished, shared by every upload entry point so the
// wording (and the fix it points to) can't drift between them.
func launcherVersionConflictMessage(ver string) string {
	return fmt.Sprintf(
		"launcher version %s is already published; bump <Version> in launcher/ChillHub/ChillHub.csproj and re-run the release",
		ver)
}

// launcherRepublishMatches reports whether a freshly extracted launcher build
// is content-identical to the one already published under this version: same
// file paths, same sizes, same blake3 — plus the same empty directories.
//
// This is what keeps launcherVersionAlreadyPublished from turning a rerun of a
// deploy that already reached this step into a hard failure: a CI retry, or a
// job further down the same pipeline stumbling and getting re-run, uploads
// EXACTLY the same archive again. Nothing about the 2026-08-08 incident
// applies to that case — the version's meaning on disk never changes. Two
// genuinely different builds under the same number are the incident, and stay
// a 409 (see launcherVersionAlreadyPublished).
//
// The comparison is against the manifest actually sitting on disk, not
// against anything this process remembers, so a match can be trusted even
// across separate requests or separate processes.
func (h *Handlers) launcherRepublishMatches(gid, ver string, files []manifestFile, emptyDirs []string) bool {
	existing, err := h.loadManifest(gid, ver)
	if err != nil {
		// The guard above already proved the manifest file exists; a read
		// failure here is something else going wrong. Either way equality
		// cannot be proven, so this fails closed exactly like a real mismatch.
		log.Printf("[builds] cannot read published manifest %s/%s to compare a re-upload: %v", gid, ver, err)
		return false
	}
	// The stored manifest already had state files stripped by writeManifest;
	// strip the fresh scan the same way so a re-upload isn't rejected over a
	// config.json neither copy ever actually published.
	newFiles := stripLauncherStateFiles(gid, files)
	newDirs := stripLauncherStateDirs(gid, emptyDirs)
	return manifestFilesEqual(existing.Files, newFiles) && stringSetsEqual(existing.EmptyDirs, newDirs)
}

// loadManifest reads back a previously published manifest.
func (h *Handlers) loadManifest(gid, ver string) (manifest, error) {
	b, err := os.ReadFile(filepath.Join(h.manifestsDir(gid), ver+".json"))
	if err != nil {
		return manifest{}, err
	}
	var m manifest
	if err := json.Unmarshal(b, &m); err != nil {
		return manifest{}, err
	}
	return m, nil
}

// manifestFilesEqual compares two file lists by path, size and blake3,
// ignoring order — the two scans being compared were not necessarily walked
// in the same run.
func manifestFilesEqual(a, b []manifestFile) bool {
	if len(a) != len(b) {
		return false
	}
	as := append([]manifestFile(nil), a...)
	bs := append([]manifestFile(nil), b...)
	sort.Slice(as, func(i, j int) bool { return as[i].Path < as[j].Path })
	sort.Slice(bs, func(i, j int) bool { return bs[i].Path < bs[j].Path })
	for i := range as {
		if as[i].Path != bs[i].Path || as[i].Size != bs[i].Size || as[i].Blake3 != bs[i].Blake3 {
			return false
		}
	}
	return true
}

// stringSetsEqual compares two string slices as sets, ignoring order.
func stringSetsEqual(a, b []string) bool {
	if len(a) != len(b) {
		return false
	}
	as := append([]string(nil), a...)
	bs := append([]string(nil), b...)
	sort.Strings(as)
	sort.Strings(bs)
	for i := range as {
		if as[i] != bs[i] {
			return false
		}
	}
	return true
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
	if !h.hasVersionManifest(gid, ver) {
		http.Error(w, "version manifest not found", http.StatusNotFound)
		return
	}
	dir := h.manifestsDir(gid)
	b, err := json.MarshalIndent(map[string]string{"version": ver}, "", "  ")
	if err == nil {
		err = adminutil.WriteFileAtomic(filepath.Join(dir, "latest.json"), b, contentFilePerm)
	}
	if err != nil {
		log.Printf("[builds] activate %s/%s: %v", gid, ver, err)
		http.Error(w, "failed to activate version", http.StatusInternalServerError)
		return
	}

	if gid == "launcher" {
		// The Activate click is the moment the update actually becomes visible
		// to installed clients — the build finishing earlier in CI is not.
		// Nothing in this package told anyone about THIS moment before: a build
		// could sit uploaded and unactivated indefinitely with no signal that
		// anyone was still waiting on a human to click the button.
		//
		// context.Background(), not r.Context(), deliberately: this goroutine
		// outlives the request. r.Context() is cancelled as soon as the handler
		// returns and the response is written, which would race the notify call
		// against the "_, _ = w.Write(b)" a few lines down and could cut it off
		// before lib/notify.sh even started.
		//nolint:contextcheck // see above: an inherited context would be wrong here, not just unused
		go notifyPublished(context.Background(), "chillhub-installer", ver)
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
	manDir := h.manifestsDir(gid)
	if err := h.removeVersion(gid, ver); err != nil {
		log.Printf("[builds] delete %s/%s: %v", gid, ver, err)
		http.Error(w, "failed to delete version", http.StatusInternalServerError)
		return
	}
	// adjust latest.json if it pointed to deleted version
	if readLatestVersion(manDir) == ver {
		recalcLatest(manDir)
	}
	adminutil.WriteJSON(w, map[string]string{"status": "ok"})
}

// removeVersion erases one published version: its manifest and its extracted
// content directory. gid and ver must already have passed the safety checks.
//
// A manifest that is not there is not a failure: the panel retries a delete
// that timed out, two admins can click the same row, and the mass cleanup below
// walks a directory listing that may be a moment out of date.
func (h *Handlers) removeVersion(gid, ver string) error {
	if err := os.Remove(filepath.Join(h.manifestsDir(gid), ver+".json")); err != nil {
		if !os.IsNotExist(err) {
			return err
		}
	}
	filesDir := filepath.Join(h.root, "content", gid, ver)
	if err := os.RemoveAll(filesDir); err != nil {
		// A locked file, a lost permission or a mount point under the version
		// directory leaves gigabytes on disk while the panel counts the version as
		// gone, and the free-space figure it shows drifts away from reality with
		// nothing anywhere to explain it. The journal gets the absolute path — it
		// is not public, unlike the response body.
		//
		// This is deliberately not reported as a failure: the manifest is already
		// removed, so the version has disappeared for every client, and the
		// operation the operator asked for is complete. An error would make the
		// panel retry deleting a version that is no longer in the list and turn a
		// finished job into a failure. What is left is a leftover directory for
		// the operator to clear, and the journal is where that belongs.
		log.Printf("[builds] delete content %s/%s: %v", gid, ver, err)
	}
	return nil
}

// keepBeforeActive — сколько версий, предшествующих активной, переживает
// массовую чистку. Две: на первую откатываются, если в активной обнаружился
// брак, вторая остаётся запасом на случай, что и первая окажется битой.
const keepBeforeActive = 2

// prunableVersions выбирает версии, которые массовая чистка удаляет: всё, что
// старше активной, кроме keepBeforeActive ближайших к ней.
//
// Версии НОВЕЕ активной не трогаются никогда: это залитая, но ещё не
// включённая сборка — ровно то, ради чего заливку и активацию развели по
// разным кнопкам. Если активной версии в списке нет, чистка не выбирает
// ничего: без точки отсчёта «старое» не определено.
func prunableVersions(vers []string, active string) []string {
	sorted := append([]string(nil), vers...)
	sort.SliceStable(sorted, func(i, j int) bool {
		return adminutil.CompareVersions(sorted[i], sorted[j]) < 0
	})
	idx := -1
	for i, v := range sorted {
		if v == active {
			idx = i
			break
		}
	}
	if idx < 0 {
		return nil
	}
	cut := idx - keepBeforeActive
	if cut <= 0 {
		return nil
	}
	return sorted[:cut]
}

// PruneVersions removes the old versions of one game in a single request:
// everything older than the active version except the keepBeforeActive builds
// immediately preceding it.
//
// Deleting them one row at a time was the only way before, and the launcher
// accumulates a release every few days — a couple of gigabytes each. The rule
// is fixed rather than a number in the request on purpose: the endpoint that
// wipes the most data at once should not also be the one that takes "how much
// to keep" from whoever calls it.
func (h *Handlers) PruneVersions(w http.ResponseWriter, r *http.Request) {
	if !adminutil.RequireMethod(w, r, http.MethodPost) {
		return
	}
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
	entries, err := os.ReadDir(dir)
	if err != nil {
		// Do not echo the filesystem error: it carries the absolute content root.
		log.Printf("[builds] prune %s: %v", gid, err)
		http.Error(w, "game not found", http.StatusNotFound)
		return
	}
	// Без активной версии чистить нечего и не от чего: «старое» здесь
	// отсчитывается только от неё. Молча удалить ноль версий было бы хуже
	// отказа — в панели это выглядит как «всё уже чисто».
	active := readLatestVersion(dir)
	if active == "" || !h.hasVersionManifest(gid, active) {
		http.Error(w, "no active version", http.StatusConflict)
		return
	}
	deleted := []string{}
	failed := []string{}
	for _, v := range prunableVersions(manifestVersions(entries), active) {
		// A manifest file whose name is not a usable version never came from
		// this package; it is left alone rather than turned into a path.
		if !adminutil.IsSafeVersion(v) {
			log.Printf("[builds] prune %s: skipping unsafe manifest name %q", gid, v)
			continue
		}
		if err := h.removeVersion(gid, v); err != nil {
			// One stuck version must not hide the rest: the loop goes on and the
			// answer names both halves, so the panel can say what is still there
			// instead of reporting the whole cleanup as a failure.
			log.Printf("[builds] prune %s/%s: %v", gid, v, err)
			failed = append(failed, v)
			continue
		}
		deleted = append(deleted, v)
	}
	adminutil.WriteJSON(w, map[string]any{
		"deleted": deleted,
		"failed":  failed,
		"active":  active,
	})
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

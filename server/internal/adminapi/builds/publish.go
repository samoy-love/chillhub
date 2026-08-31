package builds

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"log"
	"os"
	"path/filepath"
	"sort"
	"time"

	"ChillHub/server/internal/adminutil"
)

// Publishing a tree that was BUILT rather than UPLOADED.
//
// Modpacks are assembled on the server: the mods package resolves a dependency
// tree, downloads a hundred archives and lays their files out. What comes out
// is an ordinary version directory that has to be hashed, promoted and
// described by a manifest — exactly what every ZIP upload already does after
// unzipping.
//
// So the seam is placed right there, after extraction: the three functions
// below are the upload pipeline's own steps (stage, hash+promote+manifest,
// discard) with the archive handling removed. Nothing about the manifest
// format, the atomic promote, the publish lock or the validation is
// reimplemented anywhere else — a second implementation of "what a published
// version looks like" is precisely the thing the client would start rejecting
// at some later date, on some machine nobody is watching.

// Namespace separates one family of versions from another inside the content
// root. The empty namespace is a game's own builds, which keep their historic
// layout (manifests/{gameId}, content/{gameId}) byte for byte.
type Namespace string

const (
	// NamespaceGame is a game's own build. Paths are unchanged.
	NamespaceGame Namespace = ""

	// NamespaceMods holds modpack versions:
	//
	//	manifests/_mods/{gameId}/{version}.json
	//	content/_mods/{gameId}/{version}/files/
	//
	// A separate subtree rather than a version of the game, because the two are
	// installed into the SAME folder on the player's disk from two independent
	// manifests, and mixing them in one directory would make "which versions
	// does this game have" ambiguous for every reader — the panel, the public
	// API and the cleanup jobs alike.
	//
	// The leading underscore matches the existing _registry convention and
	// cannot collide with a real game id: adminutil.IsSafeGameID accepts it,
	// so games.Save refuses it explicitly instead.
	NamespaceMods Namespace = "_mods"
)

// ManifestsDirFor returns the directory holding {version}.json and latest.json.
func (h *Handlers) ManifestsDirFor(ns Namespace, gid string) string {
	if ns == NamespaceGame {
		return filepath.Join(h.root, "manifests", gid)
	}
	return filepath.Join(h.root, "manifests", string(ns), gid)
}

// ContentDirFor returns the directory holding the published version trees.
func (h *Handlers) ContentDirFor(ns Namespace, gid string) string {
	if ns == NamespaceGame {
		return filepath.Join(h.root, "content", gid)
	}
	return filepath.Join(h.root, "content", string(ns), gid)
}

// StagedTree is a version being assembled on disk.
type StagedTree struct {
	// Dir is the staging directory; it is renamed into place on publish.
	Dir string
	// FilesRoot is where the version's files go. Everything written here
	// becomes the published tree, verbatim.
	FilesRoot string

	ns      Namespace
	gameID  string
	version string
}

// StageTree creates a staging directory on the same volume as the final one,
// so publication is a rename rather than a copy.
//
// gid and version must already have passed adminutil.IsSafeGameID and
// IsSafeVersion; this is not the place that decides whether a caller-supplied
// id may become a path.
func (h *Handlers) StageTree(ns Namespace, gid, version string) (*StagedTree, error) {
	if !adminutil.IsSafeGameID(gid) {
		return nil, fmt.Errorf("builds: unsafe gameId %q", gid)
	}
	if !adminutil.IsSafeVersion(version) {
		return nil, fmt.Errorf("builds: unsafe version %q", version)
	}
	parent := h.ContentDirFor(ns, gid)
	if err := os.MkdirAll(parent, contentDirPerm); err != nil {
		return nil, err
	}
	stageDir := filepath.Join(parent, version+".tmp-"+adminutil.GenID())
	filesRoot := filepath.Join(stageDir, "files")
	if err := os.MkdirAll(filesRoot, contentDirPerm); err != nil {
		return nil, err
	}
	return &StagedTree{
		Dir: stageDir, FilesRoot: filesRoot,
		ns: ns, gameID: gid, version: version,
	}, nil
}

// Discard removes a staging directory that will not be published. Safe to call
// after a successful Publish, where it becomes a no-op.
func (s *StagedTree) Discard() {
	if s == nil || s.Dir == "" {
		return
	}
	if err := os.RemoveAll(s.Dir); err != nil && !os.IsNotExist(err) {
		log.Printf("[builds] discard staged tree %s: %v", s.Dir, err)
	}
}

// PublishResult reports what a publication produced.
type PublishResult struct {
	Version      string
	Files        int
	Bytes        int64
	ManifestPath string

	// TreeDigest identifies the CONTENT of the published tree, independent of
	// its version name. See treeDigest.
	TreeDigest string
}

// treeDigest fingerprints what a version actually contains.
//
// ИМЕНИ ВЕРСИИ НЕДОСТАТОЧНО, И ЭТО НЕ ТЕОРИЯ.
//
// Модпак публикуется под именем вида «Автор-Пак-9.5.0», и это имя пакета на
// Thunderstore, а не номер нашей сборки. Пересобрав тот же пак изменившимся
// конвейером, мы публикуем ДРУГОЕ дерево под ТЕМ ЖЕ именем — а лаунчер решал
// «нужно ли обновиться», сравнивая ровно имена. Исправленная раскладка так и
// осталась бы на сервере, а у игроков лежала бы прежняя.
//
// Считается по путям и хешам файлов, отсортированным: время сборки сюда не
// входит намеренно. Пересборка, давшая тот же результат, обязана оставить
// отпечаток прежним, иначе каждая пересборка звала бы всех игроков обновляться
// впустую — и звала бы зря ровно столько раз, сколько нужно, чтобы на это
// перестали обращать внимание.
func treeDigest(files []manifestFile) string {
	sorted := make([]manifestFile, len(files))
	copy(sorted, files)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i].Path < sorted[j].Path })

	h := sha256.New()
	for _, f := range sorted {
		_, _ = io.WriteString(h, f.Path)
		_, _ = io.WriteString(h, "\n")
		_, _ = io.WriteString(h, f.Blake3)
		_, _ = io.WriteString(h, "\n")
	}
	// Половины хеша хватает: это метка «то же самое или другое», а не защита от
	// подбора. Короткая метка ещё и читается в логе целиком.
	return hex.EncodeToString(h.Sum(nil))[:32]
}

// Publish hashes the staged tree, promotes it over any previous copy of the
// same version and writes the manifest.
//
// onFile, when set, is called for every hashed file, which is what lets a
// streaming endpoint report progress on a tree of several thousand files.
//
// updateLatest is deliberately a parameter rather than always true: a modpack
// is built first and activated by a separate, explicit operator action, so that
// a pack which resolved with missing mods never reaches players just because
// the download finished.
func (h *Handlers) Publish(s *StagedTree, updateLatest bool, onFile func(path string, size int64)) (PublishResult, error) {
	var res PublishResult
	if s == nil {
		return res, errors.New("builds: no staged tree")
	}

	var cb func(manifestFile)
	if onFile != nil {
		cb = func(mf manifestFile) { onFile(mf.Path, mf.Size) }
	}
	files, emptyDirs, err := walkManifest(s.FilesRoot, cb)
	if err != nil {
		return res, fmt.Errorf("builds: hashing the staged tree: %w", err)
	}
	if len(files) == 0 {
		// An empty version would tell every client to delete everything it has
		// for this manifest. That is never what a finished build looks like.
		return res, errors.New("builds: staged tree is empty, refusing to publish")
	}

	var total int64
	for _, f := range files {
		total += f.Size
	}

	// The lock covers promote + manifest write, exactly as the upload paths do:
	// two publications of the same version otherwise destroy each other's
	// backup, and the winner of the content rename is not necessarily the one
	// that writes the manifest last.
	unlock := lockPublish(string(s.ns)+"/"+s.gameID, s.version)
	defer unlock()

	finalDir := filepath.Join(h.ContentDirFor(s.ns, s.gameID), s.version)
	if err := promoteVersionDir(s.Dir, finalDir); err != nil {
		return res, fmt.Errorf("builds: promoting the version directory: %w", err)
	}
	s.Dir = "" // promoted; Discard must not delete the live version

	m := manifest{
		Version:   s.version,
		BuildID:   s.version,
		GameID:    s.gameID,
		CreatedAt: time.Now().UTC().Format(time.RFC3339),
		Files:     files,
		EmptyDirs: emptyDirs,
	}
	path, _, err := h.writeManifestTo(h.ManifestsDirFor(s.ns, s.gameID), m, updateLatest)
	if err != nil {
		return res, err
	}

	return PublishResult{
		Version: s.version, Files: len(files), Bytes: total, ManifestPath: path,
		TreeDigest: treeDigest(files),
	}, nil
}

// ActivateVersion points a namespace's latest.json at an existing version.
func (h *Handlers) ActivateVersion(ns Namespace, gid, version string) error {
	if !adminutil.IsSafeGameID(gid) || !adminutil.IsSafeVersion(version) {
		return errors.New("builds: unsafe gameId/version")
	}
	dir := h.ManifestsDirFor(ns, gid)
	if _, err := os.Stat(filepath.Join(dir, version+".json")); err != nil {
		return fmt.Errorf("builds: version %q has no manifest", version)
	}
	return writeLatestJSON(dir, version)
}

// LatestVersion reports what latest.json points at, or "" when nothing is
// active yet.
func (h *Handlers) LatestVersion(ns Namespace, gid string) string {
	return readLatestVersion(h.ManifestsDirFor(ns, gid))
}

// ListPublished returns the versions that have a manifest, newest first by
// name order reversed is NOT assumed — the caller sorts, because modpack
// versions carry a package name and do not order like game builds.
func (h *Handlers) ListPublished(ns Namespace, gid string) ([]string, error) {
	entries, err := os.ReadDir(h.ManifestsDirFor(ns, gid))
	if err != nil {
		if os.IsNotExist(err) {
			return nil, nil
		}
		return nil, err
	}
	return manifestVersions(entries), nil
}

// VersionStats reports when a published version was built, how many files it
// lists and their total size.
func (h *Handlers) VersionStats(ns Namespace, gid, version string) (createdAt string, files int, bytes int64) {
	return manifestStats(filepath.Join(h.ManifestsDirFor(ns, gid), version+".json"))
}

// DeletePublished removes one version: its manifest and its extracted tree.
//
// The active version is refused: deleting what latest.json points at leaves
// every client asking for a manifest that is gone.
func (h *Handlers) DeletePublished(ns Namespace, gid, version string) error {
	if !adminutil.IsSafeGameID(gid) || !adminutil.IsSafeVersion(version) {
		return errors.New("builds: unsafe gameId/version")
	}
	if h.LatestVersion(ns, gid) == version {
		return fmt.Errorf("builds: version %q is the active one; activate another version first", version)
	}

	manifestsRoot := filepath.Join(h.root, "manifests")
	contentRoot := filepath.Join(h.root, "content")
	manPath := filepath.Join(h.ManifestsDirFor(ns, gid), version+".json")
	conDir := filepath.Join(h.ContentDirFor(ns, gid), version)
	if !adminutil.EnsureWithin(manifestsRoot, manPath) || !adminutil.EnsureWithin(contentRoot, conDir) {
		return errors.New("builds: refusing to delete outside the content root")
	}

	// The manifest goes first: once it is gone the version is invisible to
	// every reader, and a failure to remove the tree leaves a stray directory
	// rather than a listed version whose files vanished underneath it.
	if err := os.Remove(manPath); err != nil && !os.IsNotExist(err) {
		return err
	}
	if err := os.RemoveAll(conDir); err != nil {
		log.Printf("[builds] delete %s %s/%s content: %v", ns, gid, version, err)
	}
	return nil
}

// SpaceBudgetFor reports how many bytes may be written under dir before the
// reserve is eaten, and whether the volume could be measured at all.
//
// Exported for the modpack builder, which has to decide whether to start a
// 1.8 GB download BEFORE the first byte lands rather than discover the answer
// halfway through. Same reserve and same "unmeasurable is not full" rule the
// upload paths use, because a second opinion about free space is how one of
// the two ends up wrong.
func SpaceBudgetFor(dir string) (uint64, bool) {
	return spaceBudget(dir)
}

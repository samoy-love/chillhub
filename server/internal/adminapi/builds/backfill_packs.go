package builds

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strings"

	"ChillHub/server/internal/adminutil"
)

// SmallPacksBackfillResult describes the packages available after the run.
// A run can repair files while leaving an identical manifest in place.
type SmallPacksBackfillResult struct {
	Packs           int
	Files           int
	ManifestUpdated bool
}

// BackfillSmallPacks adds small-file packages to an existing game or modpack
// version. The publisher verifies each source file and writes the packages
// before they are advertised. The version pointer and modpack source stay intact.
func (h *Handlers) BackfillSmallPacks(ns Namespace, gid, ver string) (SmallPacksBackfillResult, error) {
	var res SmallPacksBackfillResult
	if (ns != NamespaceGame && ns != NamespaceMods) ||
		!adminutil.IsCatalogGameID(gid) || !adminutil.IsSafeVersion(ver) || strings.EqualFold(gid, LauncherGameID) {
		return res, fmt.Errorf("unsupported package backfill target %q %q/%q", ns, gid, ver)
	}

	manDir := h.ManifestsDirFor(ns, gid)
	unlock := lockPublish(manDir)
	defer unlock()

	root, err := filepath.EvalSymlinks(h.root)
	if err != nil {
		return res, err
	}
	manDir, err = backfillPackDirectory(root, manDir)
	if err != nil {
		return res, err
	}
	manPath := filepath.Join(manDir, ver+".json")
	m, before, err := readPackBackfillManifest(manPath)
	if err != nil {
		return res, err
	}
	if m.Version != ver || m.GameID != gid {
		return res, fmt.Errorf("%s describes %s/%s, not %s/%s", manPath, m.GameID, m.Version, gid, ver)
	}
	previous := m.SmallPacks
	m.SmallPacks = nil
	m, err = prepareManifest(m)
	if err != nil {
		return res, err
	}
	versionDir, err := backfillPackDirectory(root, filepath.Join(h.ContentDirFor(ns, gid), ver))
	if err != nil {
		return res, err
	}
	m.SmallPacks, err = writeSmallPacks(versionDir, m)
	if err != nil {
		return res, err
	}

	backfillHashed()
	if slices.EqualFunc(previous, m.SmallPacks, func(a, b smallFilePack) bool {
		return a.SHA256 == b.SHA256 && slices.Equal(a.Files, b.Files)
	}) {
		if err := requireUnchangedManifest(manPath, before); err != nil {
			return res, err
		}
	} else {
		if err := h.writeIfUnchanged(manDir, manPath, before, m); err != nil {
			return res, err
		}
		res.ManifestUpdated = true
	}
	res.Packs = len(m.SmallPacks)
	for _, pack := range m.SmallPacks {
		res.Files += len(pack.Files)
	}
	return res, nil
}

func readPackBackfillManifest(path string) (manifest, []byte, error) {
	var m manifest
	info, err := os.Lstat(path)
	if err != nil {
		return m, nil, err
	}
	if !info.Mode().IsRegular() {
		return m, nil, fmt.Errorf("%s is not a regular manifest file", path)
	}
	before, err := os.ReadFile(path)
	if err != nil {
		return m, nil, err
	}
	if err := json.Unmarshal(before, &m); err != nil {
		return m, nil, fmt.Errorf("parse %s: %w", path, err)
	}
	return m, before, nil
}

func backfillPackDirectory(root, dir string) (string, error) {
	resolved, err := filepath.EvalSymlinks(dir)
	if err != nil {
		return "", err
	}
	if !adminutil.EnsureStrictlyWithin(root, resolved) {
		return "", fmt.Errorf("package backfill target %q is outside the content root", dir)
	}
	return resolved, nil
}

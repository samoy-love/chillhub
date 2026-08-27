package mods

import (
	"archive/zip"
	"errors"
	"fmt"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"

	"ChillHub/server/internal/adminutil"
)

// This file lays a downloaded package archive out inside the game folder the
// way BepInEx expects to find it.
//
// The rules are not invented here — they come from the game's entry in the
// Thunderstore ecosystem schema, so one implementation covers the 235 BepInEx
// games in the catalogue. What the rules say, in practice, for a BepInEx game:
//
//	BepInEx/plugins   default location, per-mod subfolder, nested dirs flattened
//	BepInEx/core      per-mod subfolder
//	BepInEx/patchers  per-mod subfolder
//	BepInEx/monomod   per-mod subfolder, claims *.mm.dll
//	BepInEx/config    NO per-mod subfolder — every mod's config lands together
//
// and a mod-loader package (BepInEx itself) is unpacked at the root of the
// game instead, which is what puts winhttp.dll and doorstop_config.ini next to
// the executable.

const (
	// dirPerm/filePerm match the rest of contentRoot: nginx serves this tree
	// straight off disk.
	dirPerm  = 0o755
	filePerm = 0o644

	// maxEntryPathLen bounds one path inside an archive. Nothing legitimate
	// comes close, and Windows clients have their own limits well below this.
	maxEntryPathLen = 512
)

// basePackageFiles are the files every Thunderstore package must contain. They
// are skipped at the archive root during layout, and JunkNames sweeps any that
// survive deeper in the tree.
var basePackageFiles = map[string]bool{
	"manifest.json": true,
	"readme.md":     true,
	"icon.png":      true,
}

// JunkNames are files that belong to a package's presentation on Thunderstore
// and mean nothing to a player. They are removed RECURSIVELY from the finished
// tree, by exact name, case-insensitively.
//
// Exact name, not a pattern: "README.es.md" is a translation a mod author
// shipped on purpose and stays. Matching by prefix or extension would delete
// it, and the difference is invisible until somebody notices their mod lost a
// file.
var JunkNames = map[string]bool{
	"changelog.md":  true,
	"icon.png":      true,
	"manifest.json": true,
	"readme.md":     true,
	"license":       true,
	"license.txt":   true,
}

// Layout describes where a package's files go for one game.
type Layout struct {
	rules       []InstallRule
	defaultRule InstallRule
	monomodRule *InstallRule
	// byLeaf maps the last segment of a route ("plugins", "config") to its
	// rule, which is how a folder named "config" inside an archive is
	// recognised as "this is the config route" rather than mod content.
	byLeaf map[string]InstallRule
}

// NewLayout prepares the rules of one game for repeated use.
func NewLayout(def R2modmanDef) (*Layout, error) {
	if len(def.InstallRules) == 0 {
		return nil, errors.New("mods: game has no install rules in the ecosystem schema")
	}
	l := &Layout{rules: def.InstallRules, byLeaf: make(map[string]InstallRule, len(def.InstallRules))}
	for _, r := range def.InstallRules {
		leaf := routeLeaf(r.Route)
		if leaf != "" {
			l.byLeaf[leaf] = r
		}
		if r.IsDefaultLocation {
			l.defaultRule = r
		}
		for _, ext := range r.DefaultFileExtensions {
			if strings.EqualFold(ext, ".mm.dll") {
				rule := r
				l.monomodRule = &rule
			}
		}
	}
	if l.defaultRule.Route == "" {
		// Without a default location there is nowhere to put an ordinary DLL.
		// Falling back to the first rule would silently install plugins into
		// whatever happens to be listed first, so refuse instead.
		return nil, errors.New("mods: game has no default install location")
	}
	return l, nil
}

func routeLeaf(route string) string {
	parts := strings.Split(strings.Trim(strings.ReplaceAll(route, "\\", "/"), "/"), "/")
	if len(parts) == 0 {
		return ""
	}
	return strings.ToLower(parts[len(parts)-1])
}

// InstallPackage extracts one package archive into root according to the
// layout. budget caps the total bytes written across the whole build.
//
// Returns the number of files written.
func (l *Layout) InstallPackage(root string, pkg ResolvedPackage, zipPath string, budget *adminutil.ExtractBudget) (int, error) {
	zr, err := zip.OpenReader(zipPath)
	if err != nil {
		return 0, fmt.Errorf("mods: open %s: %w", pkg.FullName, err)
	}
	defer func() { _ = zr.Close() }()

	subdir := pkg.Namespace + "-" + pkg.Name
	written := 0

	for _, f := range zr.File {
		rel := normalizeEntry(f.Name)
		if rel == "" || strings.HasSuffix(f.Name, "/") {
			continue
		}
		if err := entryProblem(rel); err != nil {
			return written, fmt.Errorf("mods: %s: %w", pkg.FullName, err)
		}

		dest, keep := l.destination(pkg, rel, subdir)
		if !keep {
			continue
		}

		full := filepath.Join(root, filepath.FromSlash(dest))
		if !adminutil.EnsureWithin(root, full) {
			return written, fmt.Errorf("mods: %s: entry %q escapes the target directory", pkg.FullName, rel)
		}
		if err := writeEntry(f, full, budget); err != nil {
			return written, fmt.Errorf("mods: %s: %s: %w", pkg.FullName, rel, err)
		}
		written++
	}
	return written, nil
}

// destination maps one archive-relative path to a path inside the game folder.
// keep is false for entries that are dropped outright.
func (l *Layout) destination(pkg ResolvedPackage, rel, subdir string) (dest string, keep bool) {
	parts := strings.Split(rel, "/")

	if pkg.IsLoader {
		// A mod loader is unpacked at the root of the game. When the schema
		// names a root folder (BepInExPack/, BepInExPack_Valheim/, ...), only
		// that folder's contents are taken and everything beside it — the
		// package's own readme and icon — is left behind.
		if pkg.LoaderRoot != "" {
			if len(parts) < 2 || !strings.EqualFold(parts[0], pkg.LoaderRoot) {
				return "", false
			}
			parts = parts[1:]
		}
		if len(parts) == 1 && basePackageFiles[strings.ToLower(parts[0])] {
			return "", false
		}
		return path.Join(parts...), true
	}

	if len(parts) == 1 && basePackageFiles[strings.ToLower(parts[0])] {
		return "", false
	}

	// A top-level folder whose name matches a route ("plugins", "config", ...)
	// selects that route and is consumed; everything else keeps its full path
	// and goes to the default location.
	rule := l.defaultRule
	tail := parts
	if len(parts) > 1 {
		if r, ok := l.byLeaf[strings.ToLower(parts[0])]; ok {
			rule = r
			tail = parts[1:]
		}
	}
	// A .mm.dll is MonoMod's, wherever it sits — but only when the path did
	// not already name a route explicitly.
	if l.monomodRule != nil && rule.Route == l.defaultRule.Route && strings.HasSuffix(strings.ToLower(rel), ".mm.dll") {
		rule = *l.monomodRule
	}
	if len(tail) == 0 {
		return "", false
	}

	route := strings.Trim(strings.ReplaceAll(rule.Route, "\\", "/"), "/")
	switch rule.TrackingMethod {
	case "none":
		// No per-mod folder. This is what makes BepInEx/config a single shared
		// directory, and it is also why one modpack's configs overwrite the
		// defaults of the individual mods it bundles.
		return path.Join(route, path.Join(tail...)), true
	case "subdir-no-flatten", "state", "package-zip":
		return path.Join(route, subdir, path.Join(tail...)), true
	default: // "subdir" and anything unknown
		// Flattened: only the file name survives, nested folders inside the
		// package are collapsed into the mod's own directory.
		return path.Join(route, subdir, tail[len(tail)-1]), true
	}
}

// writeEntry extracts one zip entry to full.
func writeEntry(f *zip.File, full string, budget *adminutil.ExtractBudget) error {
	if err := os.MkdirAll(filepath.Dir(full), dirPerm); err != nil { // #nosec G301 -- see dirPerm
		return err
	}
	rc, err := f.Open()
	if err != nil {
		return err
	}
	defer func() { _ = rc.Close() }()

	// #nosec G304 -- full was built from a normalized, validated entry path and
	// confirmed by EnsureWithin to stay inside the target root.
	out, err := os.OpenFile(full, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, filePerm)
	if err != nil {
		return err
	}
	if err := budget.Copy(out, rc); err != nil {
		_ = out.Close()
		return err
	}
	return out.Close()
}

// normalizeEntry turns an archive entry name into a clean relative slash path,
// or "" when it cannot be one.
//
// A backslash is treated as a SEPARATOR, not as a character in a name: Windows
// cannot hold a file whose name contains one, so an entry like
// "BepInEx\plugins\a.dll" can only have meant a path — and reading it as a
// single flat file name is how a package quietly installs to the wrong place.
func normalizeEntry(name string) string {
	p := strings.ReplaceAll(strings.TrimSpace(name), "\\", "/")
	for strings.Contains(p, "//") {
		p = strings.ReplaceAll(p, "//", "/")
	}
	p = strings.Trim(p, "/")
	if p == "" || p == "." {
		return ""
	}
	return p
}

// entryProblem rejects an entry path that must never reach the filesystem.
func entryProblem(rel string) error {
	if len(rel) > maxEntryPathLen {
		return fmt.Errorf("entry path too long: %d bytes", len(rel))
	}
	for _, r := range rel {
		if r < 0x20 || r == 0x7F {
			return fmt.Errorf("control character in entry path %q", rel)
		}
	}
	if strings.ContainsRune(rel, ':') {
		return fmt.Errorf("colon in entry path %q (drive or NTFS stream)", rel)
	}
	for seg := range strings.SplitSeq(rel, "/") {
		if seg == "." || seg == ".." {
			return fmt.Errorf("relative segment in entry path %q", rel)
		}
		if seg == "" {
			return fmt.Errorf("empty segment in entry path %q", rel)
		}
		if strings.HasSuffix(seg, " ") || strings.HasSuffix(seg, ".") {
			return fmt.Errorf("segment %q ends with a space or dot", seg)
		}
	}
	return nil
}

// SweepJunk removes every JunkNames file from the tree and then every
// directory the removals emptied. Returns how many files were deleted.
//
// This runs once over the finished tree rather than per package, so it also
// catches files a loader package placed at the root and files nested inside a
// mod's own folder structure.
func SweepJunk(root string) (int, error) {
	var victims []string
	err := filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		if JunkNames[strings.ToLower(d.Name())] {
			victims = append(victims, p)
		}
		return nil
	})
	if err != nil {
		return 0, err
	}
	for _, v := range victims {
		if err := os.Remove(v); err != nil {
			return 0, fmt.Errorf("mods: remove %s: %w", v, err)
		}
	}
	if err := removeEmptyDirs(root); err != nil {
		return len(victims), err
	}
	return len(victims), nil
}

// removeEmptyDirs deletes directories left empty, deepest first. The root
// itself is never removed.
func removeEmptyDirs(root string) error {
	var dirs []string
	err := filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() && p != root {
			dirs = append(dirs, p)
		}
		return nil
	})
	if err != nil {
		return err
	}
	// Deepest first, so a directory that only contained empty directories is
	// itself empty by the time it is considered.
	sort.Slice(dirs, func(i, j int) bool { return len(dirs[i]) > len(dirs[j]) })
	for _, d := range dirs {
		entries, err := os.ReadDir(d)
		if err != nil {
			continue
		}
		if len(entries) == 0 {
			_ = os.Remove(d)
		}
	}
	return nil
}

// CountTree reports the number of files and total bytes under root. Used for
// the build report and for the operator-facing summary.
func CountTree(root string) (files int, bytes int64) {
	// A walk error aborts the count; the caller gets whatever was counted so
	// far. The tree was written by this process moments earlier, so an
	// unreadable entry is a real problem worth surfacing in the totals rather
	// than a condition to paper over.
	_ = filepath.WalkDir(root, func(_ string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		info, err := d.Info()
		if err != nil {
			return err
		}
		files++
		bytes += info.Size()
		return nil
	})
	return files, bytes
}

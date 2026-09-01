package mods

import (
	"archive/zip"
	"errors"
	"fmt"
	"log"
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

	// writers remembers which package wrote each path, and dlls which package
	// shipped each assembly file name. Both exist for the same reason, and it
	// is not bookkeeping for its own sake — see Collisions.
	writers map[string]string
	dlls    map[string][]string
	clashes []Collision

	// casing remembers, per build, which spelling of each path already exists:
	// lower-cased path -> the path actually written.
	//
	// The server's filesystem is case-sensitive and the player's is not, so an
	// archive holding both moresuits/Glow.png and moresuits/advanced/glow.png
	// produced two files here and one file there — and the manifest, which is
	// validated case-insensitively, was rejected outright:
	//
	//	duplicate path "BepInEx/plugins/x753-More_Suits/glow.png"
	//
	// Collapsing them the way the player's disk would is the only outcome that
	// can actually be delivered.
	casing map[string]string
}

// NewLayout prepares the rules of one game for repeated use.
func NewLayout(def R2modmanDef) (*Layout, error) {
	if len(def.InstallRules) == 0 {
		return nil, errors.New("mods: game has no install rules in the ecosystem schema")
	}
	l := &Layout{
		rules:   def.InstallRules,
		byLeaf:  make(map[string]InstallRule, len(def.InstallRules)),
		casing:  make(map[string]string),
		writers: make(map[string]string),
		dlls:    make(map[string][]string),
	}
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

// Collision is one place where two packages of the same build met.
type Collision struct {
	// Kind is "path" — the second package overwrote the first one's file —
	// or "assembly": two packages shipped a DLL under the same name into
	// their own folders, which collide at load time instead of on disk.
	Kind string `json:"kind"`
	// What is the path in the game folder, or the assembly file name.
	What string `json:"what"`
	// By lists the packages involved, in install order.
	By []string `json:"by"`
}

// Collisions reports where two packages of this build stepped on each other.
//
// ЗАЧЕМ ЭТО ВООБЩЕ СЧИТАЕТСЯ.
//
// Раскладка — единственное место, которое видит КАЖДЫЙ путь сборки, и до сих
// пор она молча позволяла последнему писавшему победить. Так чужой BepInEx
// переписал BepInEx/core, и заметили это только сверкой готовой папки с
// r2modman вручную. Резолвер тот случай теперь не допускает, но правило,
// закрывающее один случай, не закрывает класс: маршруты приходят из схемы
// Thunderstore и могут измениться, а модпаки собирают живые люди.
//
// Считаются две разные встречи.
//
// "path" — второй пакет записал файл поверх первого. Общие по замыслу
// маршруты (BepInEx/config, trackingMethod "none") сюда не идут: там перекрытие
// и есть смысл — конфиги модпака заменяют настройки отдельных модов.
//
// "assembly" — два пакета принесли DLL с одним именем, каждый в свою папку. На
// диске они не сталкиваются, зато сталкиваются в загрузчике: BepInEx возьмёт
// один плагин из двух. Именно так выглядел rob_gaming-Driver рядом с
// public_ParticleSystem-Driver — 66 МБ старой сборки и 403 КБ новой, обе
// DriverMod.dll. Проверка на живом модпаке из 288 пакетов и 307 имён DLL дала
// РОВНО ОДНО срабатывание — то самое. Ложных нет.
func (l *Layout) Collisions() []Collision {
	out := append([]Collision(nil), l.clashes...)
	for name, by := range l.dlls {
		if len(by) > 1 {
			out = append(out, Collision{Kind: "assembly", What: name, By: by})
		}
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].Kind != out[j].Kind {
			return out[i].Kind < out[j].Kind
		}
		return out[i].What < out[j].What
	})
	return out
}

// note records one written file against the package that wrote it.
func (l *Layout) note(pkg ResolvedPackage, dest string, shared bool) {
	if shared {
		return
	}
	key := strings.ToLower(dest)
	if first, ok := l.writers[key]; ok && first != pkg.FullName {
		l.clashes = append(l.clashes, Collision{Kind: "path", What: dest, By: []string{first, pkg.FullName}})
	} else if !ok {
		l.writers[key] = pkg.FullName
	}

	// Загрузчик сюда не идёт: его DLL — это ядро BepInEx, а не плагин, и оно
	// в сборке заведомо одно.
	if pkg.IsLoader || !strings.HasSuffix(key, ".dll") {
		return
	}
	name := path.Base(key)
	if by := l.dlls[name]; len(by) == 0 || by[len(by)-1] != pkg.FullName {
		l.dlls[name] = append(by, pkg.FullName)
	}
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

		dest, shared, keep := l.destination(pkg, rel, subdir)
		if !keep {
			continue
		}

		dest = l.sameCasing(dest)
		l.note(pkg, dest, shared)

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

// sameCasing returns the spelling already used for this path, if the tree
// holds one that differs only in case.
//
// The player's disk cannot tell "Glow.png" from "glow.png"; the server's can.
// Writing both produces a tree that no client can reproduce and a manifest the
// publisher refuses. Keeping the first spelling and letting the later entry
// overwrite it is exactly what installing on the player's machine would do.
func (l *Layout) sameCasing(dest string) string {
	key := strings.ToLower(dest)
	if kept, ok := l.casing[key]; ok {
		if kept != dest {
			log.Printf("[mods] path %q collides with %q by case only; keeping the first spelling", dest, kept)
		}
		return kept
	}
	l.casing[key] = dest
	return dest
}

// destination maps one archive-relative path to a path inside the game folder.
// keep is false for entries that are dropped outright; shared is true when the
// route holds every mod's files together on purpose, so two packages writing
// the same path there is the design and not a clash.
func (l *Layout) destination(pkg ResolvedPackage, rel, subdir string) (dest string, shared, keep bool) {
	parts := strings.Split(rel, "/")

	if pkg.IsLoader {
		// A mod loader is unpacked at the root of the game. When the schema
		// names a root folder (BepInExPack/, BepInExPack_Valheim/, ...), only
		// that folder's contents are taken and everything beside it — the
		// package's own readme and icon — is left behind.
		if pkg.LoaderRoot != "" {
			if len(parts) < 2 || !strings.EqualFold(parts[0], pkg.LoaderRoot) {
				return "", false, false
			}
			parts = parts[1:]
		}
		if len(parts) == 1 && basePackageFiles[strings.ToLower(parts[0])] {
			return "", false, false
		}
		return path.Join(parts...), false, true
	}

	if len(parts) == 1 && basePackageFiles[strings.ToLower(parts[0])] {
		return "", false, false
	}

	// The archive may name its route itself — either by the full path
	// ("BepInEx/plugins/...") or by the leaf alone ("plugins/..."). Those
	// segments are consumed; everything else goes to the default location.
	rule := l.defaultRule
	tail := parts
	named := false
	if r, n, ok := l.matchRoute(parts); ok {
		rule, tail, named = r, parts[n:], true
	}
	// A .mm.dll is MonoMod's, wherever it sits — but only when the path did
	// not already name a route explicitly. The guard has to look at named and
	// nothing else: comparing the chosen route with the default one is
	// tautologically true for a path that named the default route itself, so
	// BepInEx/plugins/CoolMod/Something.mm.dll — a plugin's own build
	// dependency, deliberately placed — was moved out of the plugin folder.
	if l.monomodRule != nil && !named && strings.HasSuffix(strings.ToLower(rel), ".mm.dll") {
		rule = *l.monomodRule
	}
	if len(tail) == 0 {
		return "", false, false
	}

	route := strings.Trim(strings.ReplaceAll(rule.Route, "\\", "/"), "/")
	switch rule.TrackingMethod {
	case "none":
		// No per-mod folder. This is what makes BepInEx/config a single shared
		// directory, and it is also why one modpack's configs overwrite the
		// defaults of the individual mods it bundles.
		return path.Join(route, path.Join(tail...)), true, true
	case "subdir-no-flatten", "state", "package-zip":
		return path.Join(route, subdir, path.Join(tail...)), false, true
	default: // "subdir" and anything unknown
		// ЧТО ИМЕННО СХЛОПЫВАЕТСЯ — не «все вложенные папки», а сам маршрут.
		//
		// Когда архив назвал маршрут, всё, что лежит НИЖЕ него, — это структура,
		// на которую мод рассчитывает: у More_Suits есть и moresuits/Glow.png, и
		// moresuits/advanced/glow.png, и это разные вещи. Схлопывание до имени
		// файла делало из них один путь в двух написаниях и роняло публикацию.
		//
		// Когда маршрут не назван, файлы лежат россыпью (DLL/, SfDesat/,
		// TolianMoons/ у Tolian_Moons) — вот их r2modman и схлопывает.
		//
		// Правило выведено не из документации, а из сверки с живой сборкой
		// lethal-company, которую разложил настоящий r2modman: на 60 пакетах
		// подряд прежнее правило совпало 41 раз, это — 60 из 60.
		if named {
			return path.Join(route, subdir, path.Join(tail...)), false, true
		}
		return path.Join(route, subdir, tail[len(tail)-1]), false, true
	}
}

// matchRoute finds the route an archive path names itself, and how many of its
// leading segments that took. Full paths are tried before bare leaves, and
// longer routes before shorter ones, so "BepInEx/plugins" wins over "plugins".
// At least one segment must remain: a path that is nothing but the route names
// a directory, not a file.
func (l *Layout) matchRoute(parts []string) (InstallRule, int, bool) {
	best := InstallRule{}
	bestN := 0
	for _, r := range l.rules {
		seg := strings.Split(strings.Trim(strings.ReplaceAll(r.Route, "\\", "/"), "/"), "/")
		if len(seg) == 0 || seg[0] == "" || len(parts) <= len(seg) {
			continue
		}
		if !equalFoldSegments(parts[:len(seg)], seg) {
			continue
		}
		if len(seg) > bestN {
			best, bestN = r, len(seg)
		}
	}
	if bestN > 0 {
		return best, bestN, true
	}
	if len(parts) > 1 {
		if r, ok := l.byLeaf[strings.ToLower(parts[0])]; ok {
			return r, 1, true
		}
	}
	return InstallRule{}, 0, false
}

func equalFoldSegments(a, b []string) bool {
	for i := range a {
		if !strings.EqualFold(a[i], b[i]) {
			return false
		}
	}
	return true
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

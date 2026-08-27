package mods

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"math"
	"os"
	"path/filepath"
	"strings"
	"time"

	"ChillHub/server/internal/adminapi/builds"
	"ChillHub/server/internal/adminutil"
)

// The build pipeline, end to end:
//
//	resolve the tree -> estimate its size -> refuse if the disk cannot hold it
//	-> download every package (cached) -> lay the files out -> sweep the
//	clutter -> hash and publish as a version -> record what it was built from
//
// Activation is NOT part of it. A pack whose resolve reported missing mods, or
// whose layout produced something unexpected, must be inspectable before any
// player receives it, so making a version live is a separate operator action.

const (
	// extractBudgetHeadroom multiplies the estimated download size to bound
	// extraction. Archives are compressed, so the tree is bigger than the sum
	// of the zips; the factor is generous because the cost of guessing low is a
	// failed build, while the cost of guessing high is only a weaker zip-bomb
	// guard on top of the free-space check that already ran.
	extractBudgetHeadroom = 8

	// minExtractBudget keeps small packs from getting an absurdly tight cap.
	minExtractBudget = 256 << 20
)

// SourceKind says where a modpack's contents came from.
type SourceKind string

const (
	// SourceThunderstore is an ordinary modpack package.
	SourceThunderstore SourceKind = "thunderstore"

	// SourceProfile is an imported r2modman profile (mods.yml / export.r2x).
	// It exists for the migration off the current "game and mods in one ZIP"
	// builds, whose mods.yml names every installed mod and its exact version.
	SourceProfile SourceKind = "r2modman"
)

// Request describes one build.
type Request struct {
	// GameID is the Chill Hub game the pack belongs to.
	GameID string
	// EcosystemGame is the game's key in the Thunderstore ecosystem schema.
	EcosystemGame string

	Kind SourceKind

	// Namespace/Name/Version identify the modpack package (SourceThunderstore).
	Namespace string
	Name      string
	Version   string

	// ProfileContent is the raw mods.yml / export.r2x (SourceProfile).
	ProfileContent string
	// ProfileVersion is the version name to publish an import under.
	ProfileVersion string
}

// VersionName is the version a build publishes under.
//
// For a Thunderstore pack it carries the package identity, not just the
// number: two different modpacks routinely publish a "1.0.0", and a directory
// listing that shows two of them with no way to tell which is which is a trap
// for whoever has to roll one back at two in the morning.
func (r Request) VersionName() string {
	if r.Kind == SourceProfile {
		return r.ProfileVersion
	}
	return r.Namespace + "-" + r.Name + "-" + r.Version
}

// DisplayName is what the launcher shows the player.
func (r Request) DisplayName() string {
	if r.Kind == SourceProfile {
		return r.ProfileVersion
	}
	return strings.ReplaceAll(r.Name, "_", " ")
}

// PackageURL is the pack's page on Thunderstore, for the panel to link to.
func (r Request) PackageURL(community string) string {
	if r.Kind == SourceProfile || community == "" {
		return ""
	}
	return fmt.Sprintf("https://thunderstore.io/c/%s/p/%s/%s/", community, r.Namespace, r.Name)
}

// Plan is the outcome of resolving without downloading anything.
type Plan struct {
	Version     string            `json:"version"`
	DisplayName string            `json:"displayName"`
	Packages    []ResolvedPackage `json:"packages"`
	Missing     []string          `json:"missing"`
	Loader      string            `json:"loader"`
	TotalBytes  int64             `json:"totalBytes"`
	CachedBytes int64             `json:"cachedBytes"`
	SpaceOK     bool              `json:"spaceOk"`
	SpaceNote   string            `json:"spaceNote,omitempty"`
}

// Source records what a published version was built from. Stored next to the
// manifest as {version}.src.json and used for the composition diff between two
// versions, which is the thing an operator actually wants to see before making
// a pack live.
type Source struct {
	Kind        SourceKind `json:"kind"`
	Version     string     `json:"version"`
	DisplayName string     `json:"displayName"`
	PackageURL  string     `json:"packageUrl,omitempty"`
	Package     string     `json:"package,omitempty"`
	BuiltAt     string     `json:"builtAt"`
	Loader      string     `json:"loader,omitempty"`
	Tree        []string   `json:"tree"`
	Missing     []string   `json:"missing,omitempty"`
	Files       int        `json:"files"`
	Bytes       int64      `json:"bytes"`
}

// Event is one line of build progress.
type Event struct {
	Type    string `json:"type"`
	Message string `json:"message,omitempty"`
	Step    int    `json:"step,omitempty"`
	Total   int    `json:"total,omitempty"`
	Bytes   int64  `json:"bytes,omitempty"`
	Version string `json:"version,omitempty"`
	Files   int    `json:"files,omitempty"`
}

// Emit reports progress. A nil emitter is fine.
type Emit func(Event)

func (e Emit) send(ev Event) {
	if e != nil {
		e(ev)
	}
}

// Builder ties the Thunderstore client, the schema, the archive cache and the
// publication pipeline together.
type Builder struct {
	Client *Client
	Eco    *EcosystemCache
	Cache  *ArchiveCache
	Builds *builds.Handlers
	Root   string
}

// NewBuilder wires a builder for one content root.
func NewBuilder(root string, b *builds.Handlers) *Builder {
	c := NewClient(nil)
	return &Builder{
		Client: c,
		Eco:    NewEcosystemCache(c, root),
		Cache:  NewArchiveCache(root),
		Builds: b,
		Root:   root,
	}
}

// roots turns a request into the dependency strings the resolve starts from.
func (b *Builder) roots(req Request) ([]string, error) {
	switch req.Kind {
	case SourceThunderstore:
		if req.Namespace == "" || req.Name == "" || req.Version == "" {
			return nil, errors.New("mods: modpack package is not fully specified")
		}
		return []string{req.Namespace + "-" + req.Name + "-" + req.Version}, nil
	case SourceProfile:
		list, err := ParseProfile(req.ProfileContent)
		if err != nil {
			return nil, err
		}
		deps := EnabledDependencies(list)
		if len(deps) == 0 {
			return nil, errors.New("mods: the imported profile has no enabled mods")
		}
		return deps, nil
	default:
		return nil, fmt.Errorf("mods: unknown source kind %q", req.Kind)
	}
}

// Resolve walks the tree and measures it without downloading anything.
func (b *Builder) Resolve(ctx context.Context, req Request) (*Plan, error) {
	return b.ResolveWith(ctx, req, nil)
}

// ResolveWith is Resolve that reports progress as it goes.
//
// Resolving a big pack costs about two minutes: a minute to walk 151 packages
// and another to ask the CDN how large each archive is, both paced to stay
// under Thunderstore's rate limit. A build spends that time before its first
// downloaded byte, so it has to say what it is doing — silence there was
// reported as the admin panel hanging on «разбор состава модпака».
func (b *Builder) ResolveWith(ctx context.Context, req Request, emit Emit) (*Plan, error) {
	if !adminutil.IsSafeVersion(req.VersionName()) {
		return nil, fmt.Errorf("mods: %q is not a usable version name", req.VersionName())
	}
	eco, err := b.Eco.Get(ctx)
	if err != nil {
		return nil, err
	}
	roots, err := b.roots(req)
	if err != nil {
		return nil, err
	}

	var prog ResolveProgress
	if emit != nil {
		prog = func(n int, dep string) {
			emit.send(Event{Type: "resolving", Step: n, Message: dep})
		}
	}
	res, err := b.Client.ResolveListWith(ctx, eco, roots, prog)
	if err != nil {
		return nil, err
	}

	plan := &Plan{
		Version:     req.VersionName(),
		DisplayName: req.DisplayName(),
		Packages:    res.Packages,
		Missing:     res.Missing,
		Loader:      res.Loader,
	}

	for i, p := range res.Packages {
		emit.send(Event{
			Type: "sizing", Step: i + 1, Total: len(res.Packages), Message: p.FullName,
		})
		if path, err := b.Cache.path(p.FullName); err == nil {
			if st, err := os.Stat(path); err == nil {
				plan.CachedBytes += st.Size()
				plan.TotalBytes += st.Size()
				continue
			}
		}
		n, err := b.Client.ArchiveSize(ctx, p.FullName)
		if err != nil {
			// A size that cannot be read is not a reason to refuse the build;
			// it only makes the estimate less exact, and the extraction budget
			// below still bounds what lands on disk.
			log.Printf("[mods] size of %s unknown: %v", p.FullName, err)
			continue
		}
		plan.TotalBytes += n
	}

	free, measured := builds.SpaceBudgetFor(b.Root)
	// free is a uint64 from the platform call; clamping keeps the comparison
	// honest on a volume so large the value would not fit a signed 64-bit int.
	freeSigned := int64(math.MaxInt64)
	if free < math.MaxInt64 {
		freeSigned = int64(free)
	}
	switch {
	case !measured:
		plan.SpaceOK = true
		plan.SpaceNote = "свободное место измерить не удалось"
	case freeSigned < plan.TotalBytes*2:
		// Twice the download: the archives land in the cache and their contents
		// land in the staged tree, and both exist at once.
		plan.SpaceOK = false
		plan.SpaceNote = fmt.Sprintf("нужно около %.1f ГБ, доступно %.1f ГБ",
			float64(plan.TotalBytes*2)/(1<<30), float64(freeSigned)/(1<<30))
	default:
		plan.SpaceOK = true
	}
	return plan, nil
}

// Build resolves, downloads, lays out and publishes a modpack version.
//
// allowMissing decides what to do when Thunderstore no longer serves some of
// the pinned mods. It defaults to refusing: a pack quietly published without
// three of its mods is a broken game that nobody knows is broken, and the
// operator is right there to make the call.
func (b *Builder) Build(ctx context.Context, req Request, allowMissing bool, emit Emit) (*Source, error) {
	version := req.VersionName()
	emit.send(Event{Type: "start", Message: "разбор состава модпака", Version: version})

	plan, err := b.ResolveWith(ctx, req, emit)
	if err != nil {
		return nil, err
	}
	if len(plan.Missing) > 0 && !allowMissing {
		return nil, fmt.Errorf("mods: %d пакетов больше нет на Thunderstore: %s",
			len(plan.Missing), strings.Join(plan.Missing, ", "))
	}
	if !plan.SpaceOK {
		return nil, fmt.Errorf("mods: на диске мало места: %s", plan.SpaceNote)
	}
	emit.send(Event{
		Type:    "resolved",
		Message: fmt.Sprintf("пакетов: %d, недоступно: %d, скачать: %.1f МБ", len(plan.Packages), len(plan.Missing), float64(plan.TotalBytes)/(1<<20)),
		Total:   len(plan.Packages),
		Bytes:   plan.TotalBytes,
	})

	game, err := b.Eco.Game(ctx, req.EcosystemGame)
	if err != nil {
		return nil, err
	}
	def, ok := game.Def()
	if !ok {
		return nil, fmt.Errorf("mods: у игры %q нет правил установки в схеме Thunderstore", req.EcosystemGame)
	}
	layout, err := NewLayout(def)
	if err != nil {
		return nil, err
	}

	staged, err := b.Builds.StageTree(builds.NamespaceMods, req.GameID, version)
	if err != nil {
		return nil, err
	}
	defer staged.Discard()

	budget := adminutil.NewExtractBudget(max(plan.TotalBytes*extractBudgetHeadroom, minExtractBudget))

	var hits int
	for i, p := range plan.Packages {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		path, hit, err := b.Cache.Fetch(ctx, b.Client, p.FullName)
		if err != nil {
			return nil, fmt.Errorf("mods: скачивание %s: %w", p.FullName, err)
		}
		if hit {
			hits++
		}
		if _, err := layout.InstallPackage(staged.FilesRoot, p, path, budget); err != nil {
			return nil, err
		}
		emit.send(Event{
			Type: "package", Step: i + 1, Total: len(plan.Packages),
			Message: p.FullName,
		})
	}
	emit.send(Event{Type: "downloaded", Message: fmt.Sprintf("из кеша взято %d пакетов из %d", hits, len(plan.Packages))})

	removed, err := SweepJunk(staged.FilesRoot)
	if err != nil {
		return nil, fmt.Errorf("mods: чистка мусора: %w", err)
	}
	emit.send(Event{Type: "swept", Message: fmt.Sprintf("вычищено лишних файлов: %d", removed), Files: removed})

	hashed := 0
	pub, err := b.Builds.Publish(staged, false, func(_ string, _ int64) {
		hashed++
		if hashed%200 == 0 {
			emit.send(Event{Type: "hashing", Step: hashed, Message: "подсчёт хешей"})
		}
	})
	if err != nil {
		return nil, err
	}

	src := &Source{
		Kind:        req.Kind,
		Version:     version,
		DisplayName: req.DisplayName(),
		BuiltAt:     time.Now().UTC().Format(time.RFC3339),
		Loader:      plan.Loader,
		Tree:        packageNames(plan.Packages),
		Missing:     plan.Missing,
		Files:       pub.Files,
		Bytes:       pub.Bytes,
	}
	if err := b.writeSource(req.GameID, version, src); err != nil {
		// The version itself is published and correct; only the sidecar that
		// powers the composition diff is missing. Say so rather than pretending
		// the build failed.
		log.Printf("[mods] %s/%s published but its source record was not stored: %v", req.GameID, version, err)
	}

	emit.send(Event{
		Type: "done", Version: version, Files: pub.Files, Bytes: pub.Bytes,
		Message: fmt.Sprintf("собрано: %d файлов, %.1f МБ", pub.Files, float64(pub.Bytes)/(1<<20)),
	})
	return src, nil
}

func packageNames(ps []ResolvedPackage) []string {
	out := make([]string, 0, len(ps))
	for _, p := range ps {
		out = append(out, p.FullName)
	}
	return out
}

// sourcePath is where a version's build record lives.
//
// A SUBDIRECTORY, not a "{version}.src.json" beside the manifests: every
// reader of a manifests directory lists its *.json files and calls each one a
// version. A sidecar sitting there shows up in the panel as a phantom version
// named "Team-Pack-1.0.0.src" — which is exactly what happened before this
// moved, and what the isolation test now guards.
func (b *Builder) sourcePath(gid, version string) string {
	return filepath.Join(b.Builds.ManifestsDirFor(builds.NamespaceMods, gid), "sources", version+".json")
}

func (b *Builder) writeSource(gid, version string, src *Source) error {
	data, err := json.MarshalIndent(src, "", "  ")
	if err != nil {
		return err
	}
	p := b.sourcePath(gid, version)
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil { // #nosec G301 -- manifests tree, nginx serves it
		return err
	}
	return adminutil.WriteFileAtomic(p, data, 0o644)
}

// ReadSource returns the build record of a published version.
func (b *Builder) ReadSource(gid, version string) (*Source, error) {
	if !adminutil.IsSafeGameID(gid) || !adminutil.IsSafeVersion(version) {
		return nil, errors.New("mods: unsafe gameId/version")
	}
	// #nosec G304 -- both components are validated slug/version values.
	data, err := os.ReadFile(b.sourcePath(gid, version))
	if err != nil {
		return nil, err
	}
	var src Source
	if err := json.Unmarshal(data, &src); err != nil {
		return nil, err
	}
	return &src, nil
}

// DeleteVersion removes a published modpack version and its build record.
func (b *Builder) DeleteVersion(gid, version string) error {
	if err := b.Builds.DeletePublished(builds.NamespaceMods, gid, version); err != nil {
		return err
	}
	if err := os.Remove(b.sourcePath(gid, version)); err != nil && !os.IsNotExist(err) {
		log.Printf("[mods] delete source record %s/%s: %v", gid, version, err)
	}
	return nil
}

// DiffEntry is one line of a composition diff between two versions.
type DiffEntry struct {
	Package string `json:"package"`
	From    string `json:"from,omitempty"`
	To      string `json:"to,omitempty"`
	Change  string `json:"change"` // added | removed | updated
}

// Diff compares what two published versions contain. This is what an operator
// reads before making a rebuild live: "which mods changed" is the question,
// and a list of 151 full names before and after does not answer it.
func (b *Builder) Diff(gid, fromVersion, toVersion string) ([]DiffEntry, error) {
	from, err := b.ReadSource(gid, fromVersion)
	if err != nil {
		return nil, fmt.Errorf("mods: состав версии %s недоступен: %w", fromVersion, err)
	}
	to, err := b.ReadSource(gid, toVersion)
	if err != nil {
		return nil, fmt.Errorf("mods: состав версии %s недоступен: %w", toVersion, err)
	}
	return diffTrees(from.Tree, to.Tree), nil
}

// diffTrees compares two lists of "Author-Mod-1.2.3" names by package identity.
func diffTrees(from, to []string) []DiffEntry {
	index := func(list []string) map[string]string {
		m := make(map[string]string, len(list))
		for _, full := range list {
			ns, name, version, ok := SplitDependency(full)
			if !ok {
				continue
			}
			m[PackageKey(ns, name)] = version
		}
		return m
	}
	label := func(list []string, key string) string {
		for _, full := range list {
			ns, name, _, ok := SplitDependency(full)
			if ok && PackageKey(ns, name) == key {
				return ns + "-" + name
			}
		}
		return key
	}

	a, bb := index(from), index(to)
	var out []DiffEntry
	for key, toVer := range bb {
		fromVer, had := a[key]
		switch {
		case !had:
			out = append(out, DiffEntry{Package: label(to, key), To: toVer, Change: "added"})
		case fromVer != toVer:
			out = append(out, DiffEntry{Package: label(to, key), From: fromVer, To: toVer, Change: "updated"})
		}
	}
	for key, fromVer := range a {
		if _, still := bb[key]; !still {
			out = append(out, DiffEntry{Package: label(from, key), From: fromVer, Change: "removed"})
		}
	}
	return out
}

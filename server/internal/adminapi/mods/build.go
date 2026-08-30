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
	"sync"
	"sync/atomic"
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

	// Roots names the dependency strings to resolve from, instead of deriving
	// them from the fields above. This is how a published version is rebuilt:
	// the record kept beside its manifest already says what the build started
	// from, and re-deriving it would quietly turn an imported profile into
	// something else.
	Roots []string
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

	// Foreign and ExtraLoaders are what the resolver refused to put in the
	// tree. Neither refuses the build — both are normal for a modpack whose
	// authors pinned a package that has since moved — but a pack laid out
	// without a mod its manifest names has to say so before it is published.
	Foreign      []string `json:"foreign,omitempty"`
	ExtraLoaders []string `json:"extraLoaders,omitempty"`

	// Roots are the dependency strings this plan was resolved from. Recorded
	// with the version so it can be rebuilt later from the same starting
	// point rather than from a guess about it.
	Roots       []string `json:"roots,omitempty"`
	TotalBytes  int64    `json:"totalBytes"`
	CachedBytes int64    `json:"cachedBytes"`
	SpaceOK     bool     `json:"spaceOk"`
	SpaceNote   string   `json:"spaceNote,omitempty"`
}

// logSkips names every package the resolver left out of the tree. One line
// each, into the server log: the build events go to whoever is watching the
// panel at that moment, and the question "почему этого мода нет в сборке"
// arrives days later.
func (p *Plan) logSkips(community string) {
	for _, dep := range p.Foreign {
		log.Printf("[mods] %s: %s не издаётся сообществом %s, в сборку не идёт",
			p.Version, dep, community)
	}
	for _, dep := range p.ExtraLoaders {
		log.Printf("[mods] %s: загрузчик уже есть (%s), %s в сборку не идёт",
			p.Version, p.Loader, dep)
	}
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

	// Foreign and ExtraLoaders are the packages the resolver left out. Kept
	// with the version rather than only in the build log: the log is gone by
	// the time somebody asks why a mod is not in the pack.
	Foreign      []string `json:"foreign,omitempty"`
	ExtraLoaders []string `json:"extraLoaders,omitempty"`

	// Roots is what the build was resolved from, and it is what «пересобрать»
	// starts from. Without it a rebuild would have to guess, and for an
	// imported profile the guess is not recoverable.
	Roots []string `json:"roots,omitempty"`

	// TreeDigest identifies what this version contains, independent of its
	// name. The launcher compares it with what it has installed: a rebuild
	// publishes a new tree under the SAME version name, and a launcher that
	// only compares names would never notice.
	TreeDigest string `json:"treeDigest,omitempty"`

	// Collisions is where two packages of this build met: same file, or same
	// assembly name in two folders. Never a reason to refuse a build — mod
	// authors bundle what they like — but the operator has to be able to see
	// it without unpacking the tree by hand.
	Collisions []Collision `json:"collisions,omitempty"`

	Files int   `json:"files"`
	Bytes int64 `json:"bytes"`
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

	// Parallel is how many archives were in flight at the moment of the event.
	// Progress that only counts finished items looks stalled while six large
	// packages are halfway through; this is what says «работа идёт».
	Parallel int `json:"parallel,omitempty"`
}

// Emit reports progress. A nil emitter is fine.
type Emit func(Event)

// serialized returns an emitter safe to call from several goroutines.
func (e Emit) serialized() Emit {
	if e == nil {
		return nil
	}
	var mu sync.Mutex
	return func(ev Event) {
		mu.Lock()
		defer mu.Unlock()
		e(ev)
	}
}

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
	cache := NewArchiveCache(root)
	c.WithMetaCache(cache.MetaDir())
	return &Builder{
		Client: c,
		Eco:    NewEcosystemCache(c, root),
		Cache:  cache,
		Builds: b,
		Root:   root,
	}
}

// roots turns a request into the dependency strings the resolve starts from.
func (b *Builder) roots(req Request) ([]string, error) {
	if len(req.Roots) > 0 {
		return append([]string(nil), req.Roots...), nil
	}
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

	// ИНДЕКС СООБЩЕСТВА — ОДИН ЗАПРОС ВМЕСТО ПОЛУТОРА СОТЕН.
	//
	// В нём лежат зависимости, адреса архивов и их размеры всех пакетов
	// сообщества сразу: и обход дерева, и оценка объёма перестают ходить в сеть
	// по одному пакету. Его неудача — не отказ сборки: без индекса всё работает
	// как раньше, только медленнее, и говорить об этом оператору нечего.
	var idx *CommunityIndex
	if req.EcosystemGame != "" {
		emit.send(Event{Type: "start", Message: "список модов сообщества"})
		got, ierr := b.Client.FetchCommunityIndex(ctx, req.EcosystemGame)
		if ierr != nil {
			log.Printf("[mods] индекс сообщества %s недоступен, разбор пойдёт по одному пакету: %v",
				req.EcosystemGame, ierr)
		} else {
			idx = got
		}
	}

	res, err := b.Client.ResolveListWithIndex(ctx, eco, roots, prog, idx)
	if err != nil {
		return nil, err
	}

	plan := &Plan{
		Version:      req.VersionName(),
		DisplayName:  req.DisplayName(),
		Packages:     res.Packages,
		Missing:      res.Missing,
		Loader:       res.Loader,
		Foreign:      res.Foreign,
		ExtraLoaders: res.ExtraLoaders,
		Roots:        res.Roots,
	}
	plan.logSkips(req.EcosystemGame)

	// ОЦЕНКА РАЗМЕРОВ ИДЁТ ПАРАЛЛЕЛЬНО.
	//
	// Это 151 запрос HEAD к хранилищу архивов — не к API, у которого свой
	// лимит. Последовательно, с паузой API между запросами, пасс занимал около
	// минуты и был ровно половиной той тишины, за которую сборку и назвали
	// зависшей. Число одновременных запросов ограничивает сам клиент.
	sizes := make([]int64, len(res.Packages))
	cached := make([]int64, len(res.Packages))
	var sizeWG sync.WaitGroup
	var sized atomic.Int64
	for i, p := range res.Packages {
		if path, err := b.Cache.path(p.FullName); err == nil {
			if st, err := os.Stat(path); err == nil {
				cached[i] = st.Size()
				sized.Add(1)
				continue
			}
		}
		// Размер архива индекс тоже знает. Это второй пасс по всему дереву,
		// который целиком перестаёт ходить в сеть.
		if v, hit := idx.Lookup(p.FullName); hit && v.FileSize > 0 {
			sizes[i] = v.FileSize
			emit.send(Event{
				Type: "sizing", Step: int(sized.Add(1)), Total: len(res.Packages), Message: p.FullName,
			})

			continue
		}

		sizeWG.Add(1)
		go func(i int, p ResolvedPackage) {
			defer sizeWG.Done()
			n, err := b.Client.ArchiveSize(ctx, p.Ref())
			if err != nil {
				// A size that cannot be read is not a reason to refuse the
				// build; it only makes the estimate less exact, and the
				// extraction budget below still bounds what lands on disk.
				log.Printf("[mods] size of %s unknown: %v", p.FullName, err)
			}
			sizes[i] = n
			emit.send(Event{
				Type: "sizing", Step: int(sized.Add(1)), Total: len(res.Packages),
				Message: p.FullName,
			})
		}(i, p)
	}
	sizeWG.Wait()
	if ctx.Err() != nil {
		return nil, ctx.Err()
	}
	for i := range res.Packages {
		plan.CachedBytes += cached[i]
		plan.TotalBytes += cached[i] + sizes[i]
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
	// СОБЫТИЯ ТЕПЕРЬ ПРИХОДЯТ ИЗ НЕСКОЛЬКИХ ГОРУТИН.
	//
	// Скачивание идёт параллельно, и каждый работник отчитывается о своём
	// пакете. Получатель на том конце — один http.ResponseWriter, писать в
	// который одновременно нельзя: это не «перепутается порядок строк», это
	// битый NDJSON и гонка, которую находит -race. Замок здесь, у источника,
	// а не у каждого вызывающего.
	emit = emit.serialized()

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
		Type: "resolved",
		Message: fmt.Sprintf("пакетов: %d, недоступно: %d, скачать: %.1f МБ",
			len(plan.Packages), len(plan.Missing), float64(plan.TotalBytes)/(1<<20)),
		Total: len(plan.Packages),
		Bytes: plan.TotalBytes,
	})
	if len(plan.Foreign) > 0 {
		emit.send(Event{
			Type: "skipped",
			Message: fmt.Sprintf("не издаётся сообществом %s, пропущено: %s",
				req.EcosystemGame, strings.Join(plan.Foreign, ", ")),
		})
	}
	if len(plan.ExtraLoaders) > 0 {
		emit.send(Event{
			Type: "skipped",
			Message: fmt.Sprintf("загрузчик уже есть (%s), пропущено: %s",
				plan.Loader, strings.Join(plan.ExtraLoaders, ", ")),
		})
	}

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

	hits, err := b.fetchAndInstall(ctx, plan, layout, staged.FilesRoot, budget, emit)
	if err != nil {
		return nil, err
	}
	emit.send(Event{Type: "downloaded", Message: fmt.Sprintf("из кеша взято %d пакетов из %d", hits, len(plan.Packages))})

	removed, err := SweepJunk(staged.FilesRoot)
	if err != nil {
		return nil, fmt.Errorf("mods: чистка мусора: %w", err)
	}
	emit.send(Event{Type: "swept", Message: fmt.Sprintf("вычищено лишних файлов: %d", removed), Files: removed})

	clashes := layout.Collisions()
	reportCollisions(version, clashes, emit)

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

		Foreign:      plan.Foreign,
		ExtraLoaders: plan.ExtraLoaders,
		Roots:        plan.Roots,
		Collisions:   clashes,
		TreeDigest:   pub.TreeDigest,
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

// reportCollisions says where two packages of the build met.
//
// Not an error and not a refusal: a modpack that ships the same library twice
// still runs, and only a person can tell a harmless duplicate from the one
// that matters. Saying nothing, though, is how a 66 МБ старый Driver пролежал
// рядом с новым, пока папку не сверили с r2modman руками.
func reportCollisions(version string, clashes []Collision, emit Emit) {
	for _, c := range clashes {
		if c.Kind == "assembly" {
			log.Printf("[mods] %s: %s принесли сразу %s", version, c.What, strings.Join(c.By, " и "))
		} else {
			log.Printf("[mods] %s: %s переписан, писали %s", version, c.What, strings.Join(c.By, " и "))
		}
	}
	if len(clashes) == 0 {
		return
	}
	emit.send(Event{
		Type:    "collision",
		Message: fmt.Sprintf("пересечений между пакетами: %d", len(clashes)),
		Total:   len(clashes),
	})
}

// fetchAndInstall downloads every package in parallel and installs them in
// plan order. Returns how many came from the cache.
//
// РАЗДЕЛЕНИЕ РОЛЕЙ ЗДЕСЬ — ЭТО НЕ УКРАШЕНИЕ.
//
// Скачивание — сеть, и его можно вести в несколько потоков: архивы лежат в
// хранилище, у которого нет того лимита, что у API. Раньше 1.8 ГБ уезжали по
// одному файлу с паузой между ними, и это была самая долгая часть сборки.
//
// Установка при этом ОСТАЁТСЯ ПОСЛЕДОВАТЕЛЬНОЙ и строго в порядке плана. Два
// пакета могут положить файл по одному и тому же пути (загрузчик и мод,
// правящий его конфиг, — обычное дело), и кто из них останется, обязано
// решать место в дереве зависимостей, а не то, чей распаковщик успел первым.
// Гонка здесь дала бы сборку, которая собирается по-разному из одного и того
// же плана.
func (b *Builder) fetchAndInstall(
	ctx context.Context,
	plan *Plan,
	layout *Layout,
	filesRoot string,
	budget *adminutil.ExtractBudget,
	emit Emit,
) (int, error) {
	n := len(plan.Packages)
	type slot struct {
		path string
		hit  bool
		err  error
	}
	got := make([]slot, n)
	ready := make([]chan struct{}, n)
	for i := range ready {
		ready[i] = make(chan struct{})
	}

	// Скачивание отменяется вместе с установкой: пакет, упавший в середине
	// плана, не повод тянуть оставшийся гигабайт.
	dlCtx, cancel := context.WithCancel(ctx)
	defer cancel()

	var inflight atomic.Int64
	var doneCount atomic.Int64
	var doneBytes atomic.Int64

	jobs := make(chan int)
	var wg sync.WaitGroup
	for range maxCDNParallel {
		wg.Go(func() {
			for i := range jobs {
				p := plan.Packages[i]
				inflight.Add(1)
				path, hit, err := b.Cache.Fetch(dlCtx, b.Client, p.Ref(),
					func(name string, attempt, of int, cause error) {
						emit.send(Event{
							Type: "retry", Step: attempt, Total: of,
							Message: fmt.Sprintf("%s — попытка %d из %d: %v", name, attempt, of, cause),
						})
					})
				inflight.Add(-1)
				got[i] = slot{path: path, hit: hit, err: err}
				close(ready[i])

				if err == nil {
					if st, statErr := os.Stat(path); statErr == nil {
						doneBytes.Add(st.Size())
					}
				}
				emit.send(Event{
					Type: "downloading", Step: int(doneCount.Add(1)), Total: n,
					Bytes:    doneBytes.Load(),
					Parallel: int(inflight.Load()),
					Message:  p.FullName,
				})
			}
		})
	}
	go func() {
		defer close(jobs)
		for i := range n {
			select {
			case jobs <- i:
			case <-dlCtx.Done():
				return
			}
		}
	}()

	hits := 0
	var failure error
	for i := range n {
		select {
		case <-ready[i]:
		case <-ctx.Done():
			failure = ctx.Err()
		}
		if failure != nil {
			break
		}
		if got[i].err != nil {
			failure = fmt.Errorf("mods: скачивание %s: %w", plan.Packages[i].FullName, got[i].err)
			break
		}
		if got[i].hit {
			hits++
		}
		if _, err := layout.InstallPackage(filesRoot, plan.Packages[i], got[i].path, budget); err != nil {
			failure = err
			break
		}
		emit.send(Event{
			Type: "package", Step: i + 1, Total: n,
			Message: plan.Packages[i].FullName,
		})
	}

	// Отменяем очередь и ДОЖИДАЕМСЯ работников в любом исходе: горутина,
	// пишущая в кеш после возврата сборки, — это временный файл, который никто
	// не уберёт, и запись в staged-дерево, которое вот-вот удалят.
	cancel()
	wg.Wait()
	return hits, failure
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

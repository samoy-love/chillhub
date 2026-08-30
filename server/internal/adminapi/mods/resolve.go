package mods

import (
	"context"
	"errors"
	"fmt"
	"sort"
	"sync"
)

// ResolvedPackage is one package of a finished dependency tree.
type ResolvedPackage struct {
	FullName     string   `json:"fullName"`
	Namespace    string   `json:"namespace"`
	Name         string   `json:"name"`
	Version      string   `json:"version"`
	Dependencies []string `json:"-"`

	// DownloadURL is the package's own archive link, straight from the API.
	//
	// It is kept because the CDN name is only USUALLY derivable from the full
	// name: Thunderstore truncates a long object name and appends a random
	// suffix, and the guessed URL then answers 403. See Client.archiveURLs.
	DownloadURL string `json:"downloadUrl,omitempty"`

	// LoaderRoot is the folder inside the archive whose contents belong at the
	// root of the game, set only for mod-loader packages. An empty string on a
	// loader means "the archive root itself".
	LoaderRoot string `json:"loaderRoot,omitempty"`
	IsLoader   bool   `json:"isLoader,omitempty"`
}

// Resolution is the outcome of walking a modpack's dependency tree.
type Resolution struct {
	// Roots are the dependency strings the walk started from: one entry for a
	// Thunderstore modpack, many for an imported r2modman profile.
	Roots []string `json:"roots"`

	// Packages is every package to install, in the order they were discovered
	// (roots first). Order is not significant to the layout engine, but a
	// stable order makes two builds of the same input comparable.
	Packages []ResolvedPackage `json:"packages"`

	// Missing lists dependency strings Thunderstore no longer serves. A build
	// may still proceed — the operator decides — but it must never be silent.
	Missing []string `json:"missing"`

	// Loader names the mod-loader package the tree is laid out with.
	Loader string `json:"loader,omitempty"`

	// Foreign lists dependencies the game's own community does not publish.
	// They are left out of the tree; resolveFrom says why.
	Foreign []string `json:"foreign,omitempty"`

	// ExtraLoaders lists further mod-loader packages the walk met after the
	// first. They are left out of the tree; resolveFrom says why.
	ExtraLoaders []string `json:"extraLoaders,omitempty"`
}

// TotalPackages is the number of packages that will actually be installed.
func (r *Resolution) TotalPackages() int { return len(r.Packages) }

// Resolve walks the dependency tree of one modpack package.
//
// The walk is breadth-first and FIRST-WINS on the package identity without its
// version: when two mods disagree about which version of a shared library they
// want, the one discovered first is installed. That is exactly what r2modman
// does, and it is not a shortcut — Thunderstore publishes no constraint
// information a solver could use, only pinned versions.
//
// Two kinds of package are dropped from the tree on top of that; resolveFrom
// explains both.
func (c *Client) Resolve(ctx context.Context, eco *Ecosystem, root string) (*Resolution, error) {
	return c.resolveFrom(ctx, eco, []string{root}, nil, nil)
}

// ResolveList walks the tree of an explicit list of pinned packages. This is
// how an imported r2modman profile is turned into a modpack: the profile names
// every mod it installed, but not the libraries those mods pull in.
func (c *Client) ResolveList(ctx context.Context, eco *Ecosystem, roots []string) (*Resolution, error) {
	return c.resolveFrom(ctx, eco, roots, nil, nil)
}

// ResolveProgress is called once per package version read, with the running
// count and the dependency string just fetched. Calls are serialised, so an
// implementation may write to a response stream without a lock of its own.
type ResolveProgress func(fetched int, dependency string)

// ResolveListWith is ResolveList that reports progress.
//
// The walk is the slow half of a build and it is slow on purpose: the client
// paces itself at roughly three requests a second because Thunderstore answers
// a burst with HTTP 429, so 151 packages take about a minute. Without a
// progress callback that minute reaches the operator as a frozen panel — which
// is exactly how the first version of this was reported as a hang.
func (c *Client) ResolveListWith(ctx context.Context, eco *Ecosystem, roots []string, prog ResolveProgress) (*Resolution, error) {
	return c.resolveFrom(ctx, eco, roots, prog, nil)
}

// ResolveListWithIndex is ResolveListWith that answers from a community index
// first. Anything the index does not hold — a dependency published in another
// community — is still fetched one by one.
func (c *Client) ResolveListWithIndex(
	ctx context.Context, eco *Ecosystem, roots []string, prog ResolveProgress, idx *CommunityIndex,
) (*Resolution, error) {
	return c.resolveFrom(ctx, eco, roots, prog, idx)
}

// ЧТО ИМЕННО НЕ ПОПАДАЕТ В ДЕРЕВО И ОТКУДА ЭТО ИЗВЕСТНО.
//
// Модпак Eclipsed_Shores для risk-of-rain-2 разложен дважды: этим конвейером и
// настоящим r2modman, папки сверены пофайлово. Из 293 пакетов разошлись два, и
// оба оказались лишними у нас:
//
//	BepInEx-BepInExPack-5.4.2304   второй загрузчик поверх bbepis-BepInExPack
//	rob_gaming-Driver-1.6.4        мод, переехавший в public_ParticleSystem
//
// Их роднит одно: в списке пакетов сообщества riskofrain2 нет ни того, ни
// другого — оба живут в других сообществах. r2modman разрешает зависимости
// только по списку сообщества и такую строку просто не находит. У нас же
// промах индекса означал запрос в общий API, а тот отдаёт любой пакет
// Thunderstore.
//
// Стоило это дорого. Первый переписал BepInEx/core: ядро 5.4.21, под которое
// собран RoR2BepInExPack, сменилось на 5.4.23, и моды перестали находить типы
// в R2API. Второй лёг рядом с public_ParticleSystem-Driver — два плагина с
// одним GUID.
//
// Отсюда два правила ниже. Молчать ни одно из них не имеет права: пакет,
// выпавший из сборки без следа, — ровно тот случай, ради которого заведено
// поле Missing.
func (c *Client) resolveFrom(
	ctx context.Context, eco *Ecosystem, roots []string, prog ResolveProgress, idx *CommunityIndex,
) (*Resolution, error) {
	if len(roots) == 0 {
		return nil, errors.New("mods: nothing to resolve")
	}

	res := &Resolution{Roots: append([]string(nil), roots...)}

	// seen keys on PackageKey (namespace+name, no version) so a second,
	// differently-versioned reference to an already-installed mod is skipped.
	seen := make(map[string]bool)
	frontier := dedupeDeps(roots, seen)

	// ПРАВИЛО ПЕРВОЕ: чего сообщество не издаёт, того в дереве нет.
	//
	// Спрашивается имя пакета без версии, а не полное имя. Пакет, у которого
	// сообщество больше не отдаёт КОНКРЕТНУЮ версию, — это прежний случай
	// Missing, и разбирается он по-прежнему общим API.
	//
	// Корни исключены: их назвал оператор. Правило целиком включается только
	// когда индекс есть, — без него список сообщества неизвестен, и отбирать
	// не по чему.
	confine := idx != nil && idx.Len() > 0

	var walked int
	for depth := 0; len(frontier) > 0; depth++ {
		if confine && depth > 0 {
			kept := frontier[:0]
			for _, dep := range frontier {
				if idx.Serves(dep) {
					kept = append(kept, dep)
					continue
				}
				res.Foreign = append(res.Foreign, dep)
			}
			frontier = kept
			if len(frontier) == 0 {
				break
			}
		}

		fetched, missing, err := c.fetchLevel(ctx, frontier, prog, &walked, idx)
		if err != nil {
			return nil, err
		}
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		res.Missing = append(res.Missing, missing...)

		var next []string
		for _, v := range fetched {
			rp := ResolvedPackage{
				FullName:     v.FullName,
				Namespace:    v.Namespace,
				Name:         v.Name,
				Version:      v.VersionNumber,
				Dependencies: v.Dependencies,
				DownloadURL:  v.DownloadURL,
			}
			if rootFolder, ok := eco.LoaderRoot(v.Namespace, v.Name); ok {
				// ПРАВИЛО ВТОРОЕ: загрузчик в сборке ровно один.
				//
				// Загрузчик распаковывается в КОРЕНЬ игры, а не в папку мода.
				// Два загрузчика — это два набора BepInEx/core поверх друг
				// друга, и остаётся тот, кто установлен позже, то есть решает
				// порядок обхода дерева. Сборка из одного и того же плана
				// переставала быть воспроизводимой.
				//
				// Остаётся первый найденный: обход идёт от корней вширь, так
				// что это ближайший к модпаку, а не случайный.
				if res.Loader != "" {
					res.ExtraLoaders = append(res.ExtraLoaders, v.FullName)
					continue
				}
				rp.IsLoader = true
				rp.LoaderRoot = rootFolder
				res.Loader = v.FullName
			}
			res.Packages = append(res.Packages, rp)
			next = append(next, v.Dependencies...)
		}
		frontier = dedupeDeps(next, seen)
	}

	sort.Strings(res.Missing)
	sort.Strings(res.Foreign)
	sort.Strings(res.ExtraLoaders)
	return res, nil
}

// dedupeDeps filters a batch of dependency strings down to the ones not seen
// before, marking them seen. Malformed strings are dropped: a dependency that
// cannot be split into namespace, name and version cannot be fetched either.
func dedupeDeps(deps []string, seen map[string]bool) []string {
	out := make([]string, 0, len(deps))
	for _, d := range deps {
		ns, name, _, ok := SplitDependency(d)
		if !ok {
			continue
		}
		key := PackageKey(ns, name)
		if seen[key] {
			continue
		}
		seen[key] = true
		out = append(out, d)
	}
	return out
}

// fetchLevel fetches one breadth-first level concurrently.
//
// Concurrency is bounded by the client's own semaphore rather than a worker
// pool here, so a level of 149 packages does not spawn 149 goroutines racing
// for 4 slots — it spawns 149 goroutines, 145 of which are parked on a channel
// send, which is cheap and keeps the code straight.
//
// A 404 becomes an entry in missing; ANY other failure — already retried four
// times with backoff inside getJSON — aborts the whole resolve. That
// distinction is the entire point: treating a timeout as "this mod no longer
// exists" is how a build succeeds while quietly dropping a third of the pack.
// Measured, not hypothetical: resolving LethalReloaded without pacing lost 59
// of 151 packages to transient errors.
func (c *Client) fetchLevel(
	ctx context.Context, deps []string, prog ResolveProgress, walked *int, idx *CommunityIndex,
) ([]*PackageVersion, []string, error) {
	type slot struct {
		v   *PackageVersion
		dep string
		err error
	}
	results := make([]slot, len(deps))

	// mu serialises the progress callback: the goroutines below finish in any
	// order, and the caller writes each report to an HTTP response.
	var mu sync.Mutex

	var wg sync.WaitGroup
	for i, dep := range deps {
		if _, _, _, ok := SplitDependency(dep); !ok {
			results[i] = slot{dep: dep, err: fmt.Errorf("malformed dependency %q", dep)}
			continue
		}

		// Индекс сообщества отвечает без сети — и без догадки о том, где в
		// имени кончается пространство имён. Это и есть та секунда вместо
		// пятидесяти, ради которой он скачивается.
		if v, hit := idx.Lookup(dep); hit {
			results[i] = slot{v: v.AsPackageVersion(), dep: dep}
			if prog != nil {
				mu.Lock()
				*walked++
				prog(*walked, dep)
				mu.Unlock()
			}

			continue
		}

		wg.Add(1)
		go func(i int, dep string) {
			defer wg.Done()
			v, err := c.GetDependency(ctx, dep)
			results[i] = slot{v: v, dep: dep, err: err}
			if prog == nil {
				return
			}
			mu.Lock()
			*walked++
			n := *walked
			prog(n, dep)
			mu.Unlock()
		}(i, dep)
	}
	wg.Wait()

	var out []*PackageVersion
	var missing []string
	for _, r := range results {
		switch {
		case r.err == nil && r.v != nil:
			out = append(out, r.v)
		case errors.Is(r.err, ErrNotFound):
			missing = append(missing, r.dep)
		case r.err != nil:
			return nil, nil, fmt.Errorf("mods: resolving %s: %w", r.dep, r.err)
		}
	}
	return out, missing, nil
}

package mods

import (
	"archive/zip"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"maps"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"ChillHub/server/internal/adminapi/builds"
)

// fakeStore serves both halves of Thunderstore: the metadata API and the CDN
// that hands out package archives.
type fakeStore struct {
	*httptest.Server

	// baseURL повторяет URL сервера: обработчики читают его при сборке
	// download_url, а поле httptest.Server к этому моменту ещё не заполнено.
	baseURL string

	deps    map[string][]string
	entries map[string]map[string]string
	hits    map[string]int

	// latest и deprecated — что Thunderstore отвечает про сам ПАКЕТ, без
	// версии: этим ответом живёт проверка обновлений модпаков.
	latest     map[string]string
	deprecated map[string]bool

	// community — полные имена версий, которые издаёт сообщество игры. Пусто
	// (обычный случай в этих тестах) — списка нет вовсе, и сборка идёт прежним
	// путём, по одному пакету через общий API.
	community map[string]bool

	// cdnDenied повторяет настоящую поломку: имя объекта в хранилище не всегда
	// выводится из полного имени пакета, и угаданный адрес отвечает 403.
	cdnDenied map[string]bool

	// failCDNTimes роняет первые N обращений к архиву — так проверяются
	// повторы, не трогая настоящую сеть.
	failCDNTimes map[string]int

	// apiHits считает запросы метаданных: по ним видно, работает ли дисковый
	// кеш версий.
	mu      sync.Mutex
	apiHits map[string]int
	dlHits  map[string]int

	lastListing string
}

func newFakeStore(t *testing.T) *fakeStore {
	t.Helper()
	fs := &fakeStore{
		latest:       map[string]string{},
		deprecated:   map[string]bool{},
		community:    map[string]bool{},
		deps:         map[string][]string{},
		entries:      map[string]map[string]string{},
		hits:         map[string]int{},
		cdnDenied:    map[string]bool{},
		failCDNTimes: map[string]int{},
		apiHits:      map[string]int{},
		dlHits:       map[string]int{},
	}
	mux := http.NewServeMux()

	mux.HandleFunc("/api/experimental/package/", func(w http.ResponseWriter, r *http.Request) {
		parts := strings.Split(strings.Trim(strings.TrimPrefix(r.URL.Path, "/api/experimental/package/"), "/"), "/")

		// Две части — сам пакет, без версии: так спрашивают «а что там сейчас
		// самое свежее». Раньше фейк на такой запрос отвечал 404, и проверка
		// обновлений в тестах не проверялась вовсе — она молча пропускала пакет.
		if len(parts) == 2 {
			key := parts[0] + "/" + parts[1]
			latest, ok := fs.latest[key]
			if !ok {
				http.NotFound(w, r)
				return
			}
			_ = json.NewEncoder(w).Encode(Package{
				Namespace:    parts[0],
				Name:         parts[1],
				FullName:     parts[0] + "-" + parts[1],
				IsDeprecated: fs.deprecated[key],
				Latest: PackageVersion{
					Namespace: parts[0], Name: parts[1], VersionNumber: latest,
					FullName: fmt.Sprintf("%s-%s-%s", parts[0], parts[1], latest), IsActive: true,
				},
			})
			return
		}
		if len(parts) < 3 {
			http.NotFound(w, r)
			return
		}
		if len(parts) == 4 && parts[3] == "readme" {
			// README есть только у существующего пакета: иначе тест на
			// отсутствующий пакет проходил бы на подделке, а не на поведении.
			if _, ok := fs.deps[fmt.Sprintf("%s-%s-%s", parts[0], parts[1], parts[2])]; !ok {
				http.NotFound(w, r)
				return
			}
			_ = json.NewEncoder(w).Encode(map[string]string{
				"markdown": "# " + parts[1] + "\n\nописание пакета",
			})
			return
		}
		full := fmt.Sprintf("%s-%s-%s", parts[0], parts[1], parts[2])
		deps, ok := fs.deps[full]
		if !ok {
			http.NotFound(w, r)
			return
		}
		fs.count(fs.apiHits, full)
		_ = json.NewEncoder(w).Encode(PackageVersion{
			Namespace: parts[0], Name: parts[1], VersionNumber: parts[2],
			FullName: full, Dependencies: deps, IsActive: true,
			// Настоящий Thunderstore отдаёт этот адрес у каждой версии, и он
			// авторитетнее угаданного имени в хранилище.
			DownloadURL: fmt.Sprintf("%s/package/download/%s/%s/%s/", fs.baseURL, parts[0], parts[1], parts[2]),
		})
	})

	// Собственная ссылка пакета: она обязана работать даже там, где угаданное
	// имя объекта в хранилище отвечает 403.
	mux.HandleFunc("/package/download/", func(w http.ResponseWriter, r *http.Request) {
		parts := strings.Split(strings.Trim(strings.TrimPrefix(r.URL.Path, "/package/download/"), "/"), "/")
		if len(parts) < 3 {
			http.NotFound(w, r)
			return
		}
		full := fmt.Sprintf("%s-%s-%s", parts[0], parts[1], parts[2])
		fs.count(fs.dlHits, full)
		fs.serveArchive(t, w, r, full)
	})

	// README, разделы сообщества и каталог: их читают эндпоинты панели.
	mux.HandleFunc("/api/cyberstorm/community/", func(w http.ResponseWriter, r *http.Request) {
		_ = json.NewEncoder(w).Encode(filtersDoc{Sections: []Section{
			{UUID: "018bb887-fa52-7236-0344-e714696ee5d5", Name: "Modpacks", Slug: "modpacks"},
		}})
	})
	mux.HandleFunc("/api/cyberstorm/listing/", func(w http.ResponseWriter, r *http.Request) {
		fs.lastListing = r.URL.Query().Encode()
		_ = json.NewEncoder(w).Encode(CatalogPage{
			Count:   1,
			Results: []CatalogEntry{{Namespace: "Team", Name: "Pack", Downloads: 100}},
		})
	})

	// Список пакетов сообщества: тот самый ответ, по которому резолвер решает,
	// издаёт ли игра пакет вообще. Пока никто не назвал состав сообщества, его
	// нет — и это ровно тот случай, когда правило «чужого не берём» выключено.
	mux.HandleFunc("/c/", func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasSuffix(r.URL.Path, "/api/v1/package/") || len(fs.community) == 0 {
			http.NotFound(w, r)
			return
		}
		fulls := slices.Sorted(maps.Keys(fs.community))
		docs := make([]map[string]any, 0, len(fulls))
		for _, full := range fulls {
			ns, name, ver, ok := SplitDependency(full)
			if !ok {
				continue
			}
			docs = append(docs, map[string]any{
				"owner": ns, "name": name, "full_name": ns + "-" + name,
				"versions": []map[string]any{{
					"namespace": ns, "name": name, "full_name": full, "version_number": ver,
					"dependencies": fs.deps[full],
					"download_url": fmt.Sprintf("%s/package/download/%s/%s/%s/", fs.baseURL, ns, name, ver),
					"file_size":    1,
				}},
			})
		}
		_ = json.NewEncoder(w).Encode(docs)
	})

	mux.HandleFunc("/cdn/", func(w http.ResponseWriter, r *http.Request) {
		full := strings.TrimSuffix(strings.TrimPrefix(r.URL.Path, "/cdn/"), ".zip")
		fs.mu.Lock()
		denied := fs.cdnDenied[full]
		// Обрыв считается только на скачивании: HEAD оценки размера ходит по
		// тем же адресам, и общий счётчик съедал бы попытки чужого шага.
		left := 0
		if r.Method == http.MethodGet {
			if left = fs.failCDNTimes[full]; left > 0 {
				fs.failCDNTimes[full] = left - 1
			}
		}
		fs.mu.Unlock()
		if denied {
			// Ровно то, что отдаёт настоящее хранилище на угаданное мимо имя:
			// 403 AccessDenied, а не 404.
			http.Error(w, "AccessDenied", http.StatusForbidden)
			return
		}
		if left > 0 {
			http.Error(w, "boom", http.StatusBadGateway)
			return
		}
		fs.count(fs.hits, full)
		fs.serveArchive(t, w, r, full)
	})

	fs.Server = httptest.NewServer(mux)
	fs.baseURL = fs.URL
	t.Cleanup(fs.Close)
	return fs
}

// count увеличивает счётчик под замком: скачивание идёт в несколько потоков.
func (fs *fakeStore) count(m map[string]int, key string) {
	fs.mu.Lock()
	m[key]++
	fs.mu.Unlock()
}

// total суммирует счётчик.
func (fs *fakeStore) total(m map[string]int) int {
	fs.mu.Lock()
	defer fs.mu.Unlock()
	n := 0
	for _, v := range m {
		n += v
	}
	return n
}

// serveArchive собирает zip пакета на лету.
func (fs *fakeStore) serveArchive(t *testing.T, w http.ResponseWriter, _ *http.Request, full string) {
	t.Helper()
	entries, ok := fs.entries[full]
	if !ok {
		http.NotFound(w, nil)
		return
	}
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	names := make([]string, 0, len(entries))
	for n := range entries {
		names = append(names, n)
	}
	sort.Strings(names)
	for _, n := range names {
		f, err := zw.Create(n)
		if err != nil {
			t.Error(err)
			return
		}
		if _, err := f.Write([]byte(entries[n])); err != nil {
			t.Error(err)
			return
		}
	}
	if err := zw.Close(); err != nil {
		t.Error(err)
		return
	}
	w.Header().Set("Content-Length", strconv.Itoa(buf.Len()))
	_, _ = w.Write(buf.Bytes())
}

// add registers a package with its dependencies and archive contents.
// setLatest говорит фейку, что Thunderstore считает свежей версией пакета.
func (fs *fakeStore) setLatest(ns, name, version string, deprecated bool) {
	fs.latest[ns+"/"+name] = version
	fs.deprecated[ns+"/"+name] = deprecated
}

func (fs *fakeStore) add(full string, deps []string, entries map[string]string) {
	fs.deps[full] = deps
	fs.entries[full] = entries
}

// testBuilder wires a Builder against the fake store and a temp content root.
func testBuilder(t *testing.T, fs *fakeStore) (*Builder, string) {
	t.Helper()
	root := t.TempDir()

	archives := NewArchiveCache(root)
	// Лестница повторов в тесте — миллисекунды: боевая занимает шесть секунд
	// на один провалившийся архив.
	archives.retryBase = 2 * time.Millisecond
	client := NewClient(fs.Client()).
		WithBases(fs.URL, fs.URL+"/cdn").
		WithInterval(time.Millisecond).
		WithMetaCache(archives.MetaDir())

	eco := &Ecosystem{
		SchemaVersion: "test",
		Games: map[string]EcoGame{
			"lethal-company": {
				Label:    "lethal-company",
				R2modman: []R2modmanDef{bepinexRules()},
			},
		},
		ModloaderPackages: []ModloaderPackage{
			{PackageID: "BepInEx-BepInExPack", RootFolder: "BepInExPack", Loader: "bepinex"},
			{PackageID: "bbepis-BepInExPack", RootFolder: "BepInExPack", Loader: "bepinex"},
		},
	}
	cache := NewEcosystemCache(client, root)
	// Prime the cache so no request for the schema reaches the fake server.
	cache.cached, cache.fetchedAt = eco, time.Now()

	return &Builder{
		Client: client,
		Eco:    cache,
		Cache:  archives,
		Builds: builds.New(root),
		Root:   root,
	}, root
}

// seedPack registers a small but realistic modpack: a root package with only
// config, one mod, and BepInEx reached transitively.
func seedPack(fs *fakeStore) {
	fs.add("Team-Pack-1.0.0", []string{"Author-CoolMod-1.0.0"}, map[string]string{
		"manifest.json":            `{"name":"Pack"}`,
		"icon.png":                 "junk",
		"README.md":                "junk",
		"config/Pack.Settings.cfg": "tuned by the pack author",
	})
	fs.add("Author-CoolMod-1.0.0", []string{"BepInEx-BepInExPack-5.4.2305"}, map[string]string{
		"CoolMod.dll":        "mod code",
		"README.md":          "junk",
		"CHANGELOG.md":       "junk",
		"LICENSE":            "junk",
		"config/CoolMod.cfg": "defaults",
	})
	fs.add("BepInEx-BepInExPack-5.4.2305", nil, map[string]string{
		"BepInExPack/winhttp.dll":                        "loader",
		"BepInExPack/doorstop_config.ini":                "enabled = true",
		"BepInExPack/.doorstop_version":                  "4.5.0",
		"BepInExPack/BepInEx/core/BepInEx.Preloader.dll": "preloader",
		"icon.png":  "junk",
		"README.md": "junk",
	})
}

func thunderstoreRequest() Request {
	return Request{
		GameID:        "lethal-company",
		EcosystemGame: "lethal-company",
		Kind:          SourceThunderstore,
		Namespace:     "Team", Name: "Pack", Version: "1.0.0",
	}
}

func TestBuildPublishesUsableTree(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, root := testBuilder(t, fs)

	src, err := b.Build(context.Background(), thunderstoreRequest(), false, nil)
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if src.Version != "Team-Pack-1.0.0" {
		// The version has to carry the package identity: two different packs
		// both publishing "1.0.0" are otherwise indistinguishable on disk.
		t.Errorf("версия %q", src.Version)
	}
	if src.Loader != "BepInEx-BepInExPack-5.4.2305" {
		t.Errorf("загрузчик %q", src.Loader)
	}
	if len(src.Tree) != 3 {
		t.Errorf("в дереве %d пакетов, ожидалось 3", len(src.Tree))
	}

	filesRoot := filepath.Join(root, "content", "_mods", "lethal-company", "Team-Pack-1.0.0", "files")
	got := treeOf(t, filesRoot)
	want := []string{
		".doorstop_version",
		"BepInEx/config/CoolMod.cfg",
		"BepInEx/config/Pack.Settings.cfg",
		"BepInEx/core/BepInEx.Preloader.dll",
		"BepInEx/plugins/Author-CoolMod/CoolMod.dll",
		"doorstop_config.ini",
		"winhttp.dll",
	}
	if strings.Join(got, "|") != strings.Join(want, "|") {
		t.Errorf("дерево:\n получено %v\n ожидалось %v", got, want)
	}

	// The manifest must exist and describe every file, with both hashes.
	manifestPath := filepath.Join(root, "manifests", "_mods", "lethal-company", "Team-Pack-1.0.0.json")
	var m struct {
		GameID string `json:"gameId"`
		Files  []struct {
			Path   string `json:"path"`
			Size   int64  `json:"size"`
			Blake3 string `json:"blake3"`
			Sha256 string `json:"sha256"`
		} `json:"files"`
	}
	readJSON(t, manifestPath, &m)
	if m.GameID != "lethal-company" {
		t.Errorf("gameId в манифесте %q", m.GameID)
	}
	if len(m.Files) != len(want) {
		t.Errorf("в манифесте %d файлов, в дереве %d", len(m.Files), len(want))
	}
	for _, f := range m.Files {
		if f.Blake3 == "" || f.Sha256 == "" {
			t.Errorf("%s без хеша — клиент откажется от такого манифеста", f.Path)
		}
	}

	// Publishing must NOT activate: a pack reaches players only when the
	// operator says so.
	if latest := b.Builds.LatestVersion(builds.NamespaceMods, "lethal-company"); latest != "" {
		t.Errorf("latest.json = %q, сборка не должна активироваться сама", latest)
	}
	if err := b.Builds.ActivateVersion(builds.NamespaceMods, "lethal-company", "Team-Pack-1.0.0"); err != nil {
		t.Fatalf("ActivateVersion: %v", err)
	}
	if latest := b.Builds.LatestVersion(builds.NamespaceMods, "lethal-company"); latest != "Team-Pack-1.0.0" {
		t.Errorf("после активации latest.json = %q", latest)
	}
}

func TestBuildIsolatedFromGameBuilds(t *testing.T) {
	// The whole point of the _mods namespace: a modpack must never show up in
	// the game's own version list, and vice versa.
	fs := newFakeStore(t)
	seedPack(fs)
	b, root := testBuilder(t, fs)

	if _, err := b.Build(context.Background(), thunderstoreRequest(), false, nil); err != nil {
		t.Fatalf("Build: %v", err)
	}
	gameVersions, err := b.Builds.ListPublished(builds.NamespaceGame, "lethal-company")
	if err != nil {
		t.Fatal(err)
	}
	if len(gameVersions) != 0 {
		t.Errorf("версии игры: %v — модпак не должен туда попадать", gameVersions)
	}
	modVersions, err := b.Builds.ListPublished(builds.NamespaceMods, "lethal-company")
	if err != nil {
		t.Fatal(err)
	}
	if len(modVersions) != 1 || modVersions[0] != "Team-Pack-1.0.0" {
		t.Errorf("версии модпака: %v", modVersions)
	}
	if _, err := os.Stat(filepath.Join(root, "manifests", "lethal-company")); !os.IsNotExist(err) {
		t.Error("сборка модпака не должна создавать каталог манифестов игры")
	}
}

func TestBuildUsesArchiveCacheOnRebuild(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, _ := testBuilder(t, fs)

	if _, err := b.Build(context.Background(), thunderstoreRequest(), false, nil); err != nil {
		t.Fatalf("первая сборка: %v", err)
	}
	firstHits := maps.Clone(fs.hits)

	if _, err := b.Build(context.Background(), thunderstoreRequest(), false, nil); err != nil {
		t.Fatalf("пересборка: %v", err)
	}
	for pkg, n := range fs.hits {
		if n != firstHits[pkg] {
			t.Errorf("%s скачан повторно (%d против %d) — кеш не сработал", pkg, n, firstHits[pkg])
		}
	}
	files, bytesHeld := b.Cache.Stats()
	if files != 3 || bytesHeld == 0 {
		t.Errorf("в кеше %d файлов, %d байт", files, bytesHeld)
	}
}

func TestBuildRefusesMissingPackagesUnlessAllowed(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	// The pack pins a mod Thunderstore no longer serves.
	fs.deps["Team-Pack-1.0.0"] = []string{"Author-CoolMod-1.0.0", "Gone-Mod-9.9.9"}
	b, _ := testBuilder(t, fs)

	_, err := b.Build(context.Background(), thunderstoreRequest(), false, nil)
	if err == nil {
		t.Fatal("сборка с пропавшими модами должна отвергаться по умолчанию")
	}
	if !strings.Contains(err.Error(), "Gone-Mod-9.9.9") {
		t.Errorf("в ошибке нет имени пропавшего пакета: %v", err)
	}

	src, err := b.Build(context.Background(), thunderstoreRequest(), true, nil)
	if err != nil {
		t.Fatalf("с allowMissing сборка должна проходить: %v", err)
	}
	if len(src.Missing) != 1 || src.Missing[0] != "Gone-Mod-9.9.9" {
		t.Errorf("пропавшие пакеты не записаны в состав: %v", src.Missing)
	}
}

func TestBuildFromR2modmanProfile(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, root := testBuilder(t, fs)

	profile := `- manifestVersion: 1
  name: Author-CoolMod
  dependencies:
    - BepInEx-BepInExPack-5.4.2305
  versionNumber:
    major: 1
    minor: 0
    patch: 0
  enabled: true
- manifestVersion: 1
  name: Disabled-Mod
  versionNumber:
    major: 3
    minor: 0
    patch: 0
  enabled: false
`
	req := Request{
		GameID: "lethal-company", EcosystemGame: "lethal-company",
		Kind:           SourceProfile,
		ProfileContent: profile,
		ProfileVersion: "import-1.0.7",
	}
	src, err := b.Build(context.Background(), req, false, nil)
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	// The disabled mod is skipped; BepInEx still arrives transitively.
	if len(src.Tree) != 2 {
		t.Errorf("дерево %v, ожидалось 2 пакета (выключенный мод пропускается)", src.Tree)
	}
	if src.Kind != SourceProfile {
		t.Errorf("Kind = %q", src.Kind)
	}
	got := treeOf(t, filepath.Join(root, "content", "_mods", "lethal-company", "import-1.0.7", "files"))
	if !contains(got, "BepInEx/plugins/Author-CoolMod/CoolMod.dll") || !contains(got, "winhttp.dll") {
		t.Errorf("дерево импорта: %v", got)
	}
}

func TestBuildEmitsProgress(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, _ := testBuilder(t, fs)

	var types []string
	_, err := b.Build(context.Background(), thunderstoreRequest(), false, func(e Event) {
		types = append(types, e.Type)
	})
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{"start", "resolved", "package", "downloaded", "swept", "done"} {
		if !contains(types, want) {
			t.Errorf("в потоке прогресса нет события %q: %v", want, types)
		}
	}
}

// TestBuildReportsResolvePhase охраняет самую длинную немую паузу сборки.
//
// Разбор состава и опрос размеров архивов занимают у большого модпака около
// двух минут — до них не доходит ни одного события "package". Ровно это и было
// прислано как «админка зависла на этапе разбор состава модпака», поэтому оба
// события обязаны идти в поток, причём с растущим счётчиком: одно событие на
// всю фазу вернуло бы ту же тишину.
func TestBuildReportsResolvePhase(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, _ := testBuilder(t, fs)

	var resolving, sizing []Event
	_, err := b.Build(context.Background(), thunderstoreRequest(), false, func(e Event) {
		switch e.Type {
		case "resolving":
			resolving = append(resolving, e)
		case "sizing":
			sizing = append(sizing, e)
		}
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(resolving) < 2 {
		t.Fatalf("обход дерева отчитался %d раз, ожидался счётчик по пакетам", len(resolving))
	}
	for i, e := range resolving {
		if e.Step != i+1 {
			t.Errorf("счётчик найденных модов идёт не по порядку: %d-е событие со Step=%d", i+1, e.Step)
		}
		if e.Message == "" {
			t.Errorf("событие %d не называет пакет", i+1)
		}
	}
	if len(sizing) != len(resolving) {
		t.Errorf("оценка размера отчиталась по %d пакетам из %d", len(sizing), len(resolving))
	}
	for i, e := range sizing {
		if e.Step != i+1 || e.Total != len(sizing) {
			t.Errorf("оценка размера: %d-е событие %d/%d", i+1, e.Step, e.Total)
		}
	}
}

// TestResolveWithoutEmitterStaysSilent: Resolve без потока событий обязан
// работать так же, как раньше, — его зовёт обычный JSON-обработчик.
func TestResolveWithoutEmitterStaysSilent(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, _ := testBuilder(t, fs)

	plan, err := b.Resolve(context.Background(), thunderstoreRequest())
	if err != nil {
		t.Fatal(err)
	}
	if len(plan.Packages) == 0 {
		t.Fatal("разбор без прогресса вернул пустой состав")
	}
}

func TestSourceRecordAndDiff(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, _ := testBuilder(t, fs)

	if _, err := b.Build(context.Background(), thunderstoreRequest(), false, nil); err != nil {
		t.Fatal(err)
	}

	// Second version: CoolMod updated, a new mod added, nothing removed.
	fs.add("Author-CoolMod-1.1.0", []string{"BepInEx-BepInExPack-5.4.2305"}, map[string]string{"CoolMod.dll": "v2"})
	fs.add("Other-Extra-2.0.0", nil, map[string]string{"Extra.dll": "extra"})
	fs.add("Team-Pack-1.1.0", []string{"Author-CoolMod-1.1.0", "Other-Extra-2.0.0"}, map[string]string{"manifest.json": "{}"})

	req2 := thunderstoreRequest()
	req2.Version = "1.1.0"
	if _, err := b.Build(context.Background(), req2, false, nil); err != nil {
		t.Fatal(err)
	}

	src, err := b.ReadSource("lethal-company", "Team-Pack-1.0.0")
	if err != nil {
		t.Fatalf("ReadSource: %v", err)
	}
	if src.Files == 0 || src.Bytes == 0 {
		t.Errorf("в записи состава нет размеров: %+v", src)
	}

	diff, err := b.Diff("lethal-company", "Team-Pack-1.0.0", "Team-Pack-1.1.0")
	if err != nil {
		t.Fatalf("Diff: %v", err)
	}
	byPkg := map[string]DiffEntry{}
	for _, d := range diff {
		byPkg[d.Package] = d
	}
	if d := byPkg["Author-CoolMod"]; d.Change != "updated" || d.From != "1.0.0" || d.To != "1.1.0" {
		t.Errorf("CoolMod: %+v, ожидалось updated 1.0.0 -> 1.1.0", d)
	}
	if d := byPkg["Other-Extra"]; d.Change != "added" || d.To != "2.0.0" {
		t.Errorf("Extra: %+v, ожидалось added 2.0.0", d)
	}
	if d, ok := byPkg["Team-Pack"]; !ok || d.Change != "updated" {
		t.Errorf("сам модпак: %+v", d)
	}
}

func TestDeleteVersionRefusesActiveOne(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, _ := testBuilder(t, fs)

	if _, err := b.Build(context.Background(), thunderstoreRequest(), false, nil); err != nil {
		t.Fatal(err)
	}
	if err := b.Builds.ActivateVersion(builds.NamespaceMods, "lethal-company", "Team-Pack-1.0.0"); err != nil {
		t.Fatal(err)
	}
	// Deleting what latest.json points at would leave every launcher asking for
	// a manifest that no longer exists.
	if err := b.DeleteVersion("lethal-company", "Team-Pack-1.0.0"); err == nil {
		t.Fatal("удаление активной версии должно отвергаться")
	}
}

func TestArchiveCacheSweepAndClear(t *testing.T) {
	root := t.TempDir()
	c := NewArchiveCache(root)
	if err := os.MkdirAll(c.Dir(), 0o755); err != nil {
		t.Fatal(err)
	}

	fresh := filepath.Join(c.Dir(), "Fresh-Mod-1.0.0.zip")
	stale := filepath.Join(c.Dir(), "Stale-Mod-1.0.0.zip")
	partial := filepath.Join(c.Dir(), cacheTmpPrefix+"abcdef123456")
	for _, p := range []string{fresh, stale, partial} {
		if err := os.WriteFile(p, []byte("0123456789"), 0o644); err != nil {
			t.Fatal(err)
		}
	}
	old := time.Now().Add(-CacheTTL - time.Hour)
	if err := os.Chtimes(stale, old, old); err != nil {
		t.Fatal(err)
	}
	if err := os.Chtimes(partial, old, old); err != nil {
		t.Fatal(err)
	}

	removed, freed := c.Sweep()
	if removed != 2 || freed != 20 {
		t.Errorf("подметено %d файлов / %d байт, ожидалось 2 / 20", removed, freed)
	}
	if _, err := os.Stat(fresh); err != nil {
		t.Error("свежий архив удалять нельзя")
	}

	removed, _ = c.Clear()
	if removed != 1 {
		t.Errorf("Clear удалил %d файлов, ожидался 1", removed)
	}
}

func TestArchiveCacheRejectsUnsafeNames(t *testing.T) {
	c := NewArchiveCache(t.TempDir())
	for _, bad := range []string{"../escape", "a/b", "", ".hidden", strings.Repeat("x", 300), `a\b`} {
		if _, err := c.path(bad); err == nil {
			t.Errorf("path(%q) принят, а не должен", bad)
		}
	}
	if _, err := c.path("BepInEx-BepInExPack-5.4.2305"); err != nil {
		t.Errorf("нормальное имя отвергнуто: %v", err)
	}
}

// --- helpers ---------------------------------------------------------------

func treeOf(t *testing.T, root string) []string {
	t.Helper()
	var out []string
	err := filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		rel, _ := filepath.Rel(root, p)
		out = append(out, filepath.ToSlash(rel))
		return nil
	})
	if err != nil {
		t.Fatalf("обход %s: %v", root, err)
	}
	sort.Strings(out)
	return out
}

func readJSON(t *testing.T, path string, v any) {
	t.Helper()
	b, err := os.ReadFile(path) // #nosec G304 -- test temp dir
	if err != nil {
		t.Fatalf("чтение %s: %v", path, err)
	}
	if err := json.Unmarshal(b, v); err != nil {
		t.Fatalf("разбор %s: %v", path, err)
	}
}

func contains(list []string, want string) bool {
	return slices.Contains(list, want)
}

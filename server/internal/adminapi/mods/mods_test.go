package mods

import (
	"archive/zip"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
	"time"

	"ChillHub/server/internal/adminutil"
)

// bepinexRules mirrors the install rules the ecosystem schema publishes for
// every BepInEx game (verified against lethal-company and how-to-fish).
func bepinexRules() R2modmanDef {
	return R2modmanDef{
		PackageLoader: "bepinex",
		InstallRules: []InstallRule{
			{Route: "BepInEx/plugins", DefaultFileExtensions: []string{".dll"}, TrackingMethod: "subdir", IsDefaultLocation: true},
			{Route: "BepInEx/core", TrackingMethod: "subdir"},
			{Route: "BepInEx/patchers", TrackingMethod: "subdir"},
			{Route: "BepInEx/monomod", DefaultFileExtensions: []string{".mm.dll"}, TrackingMethod: "subdir"},
			{Route: "BepInEx/config", TrackingMethod: "none"},
		},
	}
}

func TestSplitDependency(t *testing.T) {
	cases := []struct {
		dep               string
		ns, name, version string
		ok                bool
	}{
		{"BepInEx-BepInExPack-5.4.2305", "BepInEx", "BepInExPack", "5.4.2305", true},
		// The name itself contains hyphens. Splitting on the first two
		// separators — the obvious reading — corrupts these, and Thunderstore
		// is full of them.
		{"Linux_Squad-Enhanced_HowToFish-1.0.5", "Linux_Squad", "Enhanced_HowToFish", "1.0.5", true},
		{"Author-Some-Long-Mod-Name-1.2.3", "Author", "Some-Long-Mod-Name", "1.2.3", true},
		{"NotEnough-Parts", "", "", "", false},
		{"", "", "", "", false},
	}
	for _, c := range cases {
		ns, name, version, ok := SplitDependency(c.dep)
		if ok != c.ok || ns != c.ns || name != c.name || version != c.version {
			t.Errorf("SplitDependency(%q) = (%q,%q,%q,%v), want (%q,%q,%q,%v)",
				c.dep, ns, name, version, ok, c.ns, c.name, c.version, c.ok)
		}
	}
}

func TestLayoutDestination(t *testing.T) {
	l, err := NewLayout(bepinexRules())
	if err != nil {
		t.Fatalf("NewLayout: %v", err)
	}
	mod := ResolvedPackage{Namespace: "Author", Name: "CoolMod"}
	loader := ResolvedPackage{Namespace: "BepInEx", Name: "BepInExPack", IsLoader: true, LoaderRoot: "BepInExPack"}

	cases := []struct {
		what string
		pkg  ResolvedPackage
		rel  string
		want string
		keep bool
	}{
		{"плоский dll в plugins", mod, "CoolMod.dll", "BepInEx/plugins/Author-CoolMod/CoolMod.dll", true},

		// СХЛОПЫВАЕТСЯ МАРШРУТ, А НЕ ВЛОЖЕННОСТЬ ПОД НИМ.
		//
		// Раньше от пути оставалось только имя файла, и это ломало моды,
		// рассчитывающие на свои подпапки: у More_Suits есть и
		// moresuits/Glow.png, и moresuits/advanced/glow.png — после
		// схлопывания они превращались в один путь в двух написаниях, и
		// публикация падала на «duplicate path».
		//
		// Правило сверено с живой сборкой lethal-company, разложенной
		// настоящим r2modman: 60 пакетов подряд, 60 совпадений.
		{"вложенность под названным маршрутом сохраняется", mod, "plugins/assets/deep/thing.dll", "BepInEx/plugins/Author-CoolMod/assets/deep/thing.dll", true},
		{"маршрут полным путём тоже узнаётся", mod, "BepInEx/plugins/mymod/Glow.png", "BepInEx/plugins/Author-CoolMod/mymod/Glow.png", true},

		// Маршрут не назван — файлы лежат россыпью по папкам автора
		// (DLL/, SfDesat/, TolianMoons/ у Tolian_Moons), и вот их r2modman
		// действительно схлопывает в папку мода.
		{"россыпь без маршрута схлопывается", mod, "DLL/AmbientToggle.dll", "BepInEx/plugins/Author-CoolMod/AmbientToggle.dll", true},

		{"явный маршрут patchers", mod, "patchers/Patch.dll", "BepInEx/patchers/Author-CoolMod/Patch.dll", true},
		{"config без подкаталога мода", mod, "config/Author.CoolMod.cfg", "BepInEx/config/Author.CoolMod.cfg", true},
		{"config сохраняет вложенность", mod, "config/controls/keys.json", "BepInEx/config/controls/keys.json", true},
		{"mm.dll уходит в monomod", mod, "Something.mm.dll", "BepInEx/monomod/Author-CoolMod/Something.mm.dll", true},
		{"core по имени папки", mod, "core/Lib.dll", "BepInEx/core/Author-CoolMod/Lib.dll", true},
		{"мусор в корне пакета отбрасывается", mod, "manifest.json", "", false},
		{"README в корне отбрасывается", mod, "README.md", "", false},
		// A file that merely looks like a base package file but sits deeper is
		// content, not clutter, at layout time; SweepJunk deals with it later.
		{"вложенный manifest.json не отбрасывается на раскладке", mod, "data/manifest.json", "BepInEx/plugins/Author-CoolMod/manifest.json", true},
		{"config полным путём", mod, "BepInEx/config/mymod/keys.json", "BepInEx/config/mymod/keys.json", true},

		{"загрузчик: содержимое rootFolder в корень", loader, "BepInExPack/winhttp.dll", "winhttp.dll", true},
		{"загрузчик: вложенность сохраняется", loader, "BepInExPack/BepInEx/core/BepInEx.Preloader.dll", "BepInEx/core/BepInEx.Preloader.dll", true},
		{"загрузчик: скрытый файл версии", loader, "BepInExPack/.doorstop_version", ".doorstop_version", true},
		{"загрузчик: вне rootFolder отбрасывается", loader, "icon.png", "", false},
		{"загрузчик: README рядом с rootFolder отбрасывается", loader, "README.md", "", false},
	}

	for _, c := range cases {
		t.Run(c.what, func(t *testing.T) {
			got, _, keep := l.destination(c.pkg, c.rel, c.pkg.Namespace+"-"+c.pkg.Name)
			if keep != c.keep || (keep && got != c.want) {
				t.Errorf("destination(%q) = (%q,%v), want (%q,%v)", c.rel, got, keep, c.want, c.keep)
			}
		})
	}
}

func TestNewLayoutRejectsRulesWithoutDefault(t *testing.T) {
	def := R2modmanDef{InstallRules: []InstallRule{{Route: "BepInEx/config", TrackingMethod: "none"}}}
	if _, err := NewLayout(def); err == nil {
		t.Fatal("ожидалась ошибка: без маршрута по умолчанию обычной DLL некуда деться")
	}
	if _, err := NewLayout(R2modmanDef{}); err == nil {
		t.Fatal("ожидалась ошибка на игре без installRules")
	}
}

func TestEntryProblem(t *testing.T) {
	bad := []string{
		"../escape.dll",
		"a/../../b.dll",
		"C:/absolute.dll",
		"stream.dll:zone.identifier",
		"trailing /file.dll",
		"dotted./file.dll",
		strings.Repeat("a", maxEntryPathLen+1),
		"ctrl\x01.dll",
	}
	for _, p := range bad {
		if err := entryProblem(p); err == nil {
			t.Errorf("entryProblem(%q) = nil, ожидалась ошибка", p)
		}
	}
	good := []string{"a.dll", "BepInEx/plugins/x.dll", "..hidden/file.dll", "README.es.md"}
	for _, p := range good {
		if err := entryProblem(p); err != nil {
			t.Errorf("entryProblem(%q) = %v, ожидался успех", p, err)
		}
	}
}

func TestNormalizeEntryTreatsBackslashAsSeparator(t *testing.T) {
	// A Windows-built archive stores separators as backslashes. Reading the
	// name as one flat file is how a package silently installs to the wrong
	// place — and Windows cannot hold such a file name anyway.
	if got := normalizeEntry(`BepInEx\plugins\a.dll`); got != "BepInEx/plugins/a.dll" {
		t.Errorf("normalizeEntry = %q", got)
	}
	if got := normalizeEntry("//a//b//"); got != "a/b" {
		t.Errorf("normalizeEntry = %q", got)
	}
	if got := normalizeEntry("  /  "); got != "" {
		t.Errorf("normalizeEntry = %q, ожидалась пустая строка", got)
	}
}

func TestSweepJunk(t *testing.T) {
	root := t.TempDir()
	files := []string{
		"BepInEx/plugins/Author-Mod/Mod.dll",
		"BepInEx/plugins/Author-Mod/README.md",
		"BepInEx/plugins/Author-Mod/CHANGELOG.md",
		"BepInEx/plugins/Author-Mod/icon.png",
		"BepInEx/plugins/Author-Mod/manifest.json",
		"BepInEx/plugins/Author-Mod/LICENSE",
		"BepInEx/plugins/Author-Mod/LICENSE.txt",
		// Case must not matter.
		"BepInEx/plugins/Other-Mod/ReadMe.MD",
		"BepInEx/plugins/Other-Mod/Other.dll",
		// Exact names only: a translated readme is content the author shipped.
		"BepInEx/plugins/Other-Mod/README.es.md",
		// A directory that holds nothing but junk must disappear entirely.
		"BepInEx/plugins/Junk-Only/README.md",
		"winhttp.dll",
		"icon.png",
	}
	for _, f := range files {
		p := filepath.Join(root, filepath.FromSlash(f))
		if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(p, []byte("x"), 0o644); err != nil {
			t.Fatal(err)
		}
	}

	removed, err := SweepJunk(root)
	if err != nil {
		t.Fatalf("SweepJunk: %v", err)
	}
	if want := 9; removed != want {
		t.Errorf("удалено %d файлов, ожидалось %d", removed, want)
	}

	var left []string
	_ = filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		rel, _ := filepath.Rel(root, p)
		left = append(left, filepath.ToSlash(rel))
		return nil
	})
	sort.Strings(left)
	want := []string{
		"BepInEx/plugins/Author-Mod/Mod.dll",
		"BepInEx/plugins/Other-Mod/Other.dll",
		"BepInEx/plugins/Other-Mod/README.es.md",
		"winhttp.dll",
	}
	if strings.Join(left, "|") != strings.Join(want, "|") {
		t.Errorf("осталось %v, ожидалось %v", left, want)
	}
	if _, err := os.Stat(filepath.Join(root, "BepInEx", "plugins", "Junk-Only")); !os.IsNotExist(err) {
		t.Error("каталог из одного мусора должен быть удалён")
	}
}

// makeZip builds an in-memory package archive on disk.
func makeZip(t *testing.T, dir, fullName string, entries map[string]string) string {
	t.Helper()
	p := filepath.Join(dir, fullName+".zip")
	f, err := os.Create(p) // #nosec G304 -- test temp dir
	if err != nil {
		t.Fatal(err)
	}
	defer func() { _ = f.Close() }()
	zw := zip.NewWriter(f)
	names := make([]string, 0, len(entries))
	for n := range entries {
		names = append(names, n)
	}
	sort.Strings(names)
	for _, n := range names {
		w, err := zw.Create(n)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := w.Write([]byte(entries[n])); err != nil {
			t.Fatal(err)
		}
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return p
}

func TestInstallPackageEndToEnd(t *testing.T) {
	l, err := NewLayout(bepinexRules())
	if err != nil {
		t.Fatal(err)
	}
	zips := t.TempDir()
	root := t.TempDir()
	budget := adminutil.NewExtractBudget(1 << 20)

	loaderZip := makeZip(t, zips, "BepInEx-BepInExPack-5.4.2305", map[string]string{
		"BepInExPack/winhttp.dll":                        "dll",
		"BepInExPack/doorstop_config.ini":                "ini",
		"BepInExPack/.doorstop_version":                  "4.5.0",
		"BepInExPack/BepInEx/core/BepInEx.Preloader.dll": "pre",
		"BepInExPack/BepInEx/config/BepInEx.cfg":         "cfg",
		"icon.png":                                       "junk",
		"manifest.json":                                  "junk",
	})
	modZip := makeZip(t, zips, "Author-CoolMod-1.0.0", map[string]string{
		"CoolMod.dll":            "dll",
		"assets/nested/data.bin": "bin",
		"config/CoolMod.cfg":     "cfg",
		"README.md":              "junk",
	})

	loader := ResolvedPackage{FullName: "BepInEx-BepInExPack-5.4.2305", Namespace: "BepInEx", Name: "BepInExPack", IsLoader: true, LoaderRoot: "BepInExPack"}
	mod := ResolvedPackage{FullName: "Author-CoolMod-1.0.0", Namespace: "Author", Name: "CoolMod"}

	if _, err := l.InstallPackage(root, loader, loaderZip, budget); err != nil {
		t.Fatalf("установка загрузчика: %v", err)
	}
	if _, err := l.InstallPackage(root, mod, modZip, budget); err != nil {
		t.Fatalf("установка мода: %v", err)
	}
	if _, err := SweepJunk(root); err != nil {
		t.Fatal(err)
	}

	want := []string{
		".doorstop_version",
		"BepInEx/config/BepInEx.cfg",
		"BepInEx/config/CoolMod.cfg",
		"BepInEx/core/BepInEx.Preloader.dll",
		"BepInEx/plugins/Author-CoolMod/CoolMod.dll",
		"BepInEx/plugins/Author-CoolMod/data.bin",
		"doorstop_config.ini",
		"winhttp.dll",
	}
	var got []string
	_ = filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		rel, _ := filepath.Rel(root, p)
		got = append(got, filepath.ToSlash(rel))
		return nil
	})
	sort.Strings(got)
	if strings.Join(got, "|") != strings.Join(want, "|") {
		t.Errorf("дерево:\n получено %v\n ожидалось %v", got, want)
	}
}

func TestInstallPackageRefusesZipSlip(t *testing.T) {
	l, _ := NewLayout(bepinexRules())
	zips := t.TempDir()
	root := t.TempDir()
	p := makeZip(t, zips, "Evil-Mod-1.0.0", map[string]string{"../../escape.dll": "x"})
	pkg := ResolvedPackage{FullName: "Evil-Mod-1.0.0", Namespace: "Evil", Name: "Mod"}
	if _, err := l.InstallPackage(root, pkg, p, adminutil.NewExtractBudget(1<<20)); err == nil {
		t.Fatal("выход за пределы каталога должен быть ошибкой")
	}
}

func TestInstallPackageRespectsBudget(t *testing.T) {
	l, _ := NewLayout(bepinexRules())
	zips := t.TempDir()
	root := t.TempDir()
	p := makeZip(t, zips, "Big-Mod-1.0.0", map[string]string{"Big.dll": strings.Repeat("x", 4096)})
	pkg := ResolvedPackage{FullName: "Big-Mod-1.0.0", Namespace: "Big", Name: "Mod"}
	if _, err := l.InstallPackage(root, pkg, p, adminutil.NewExtractBudget(16)); err == nil {
		t.Fatal("превышение бюджета распаковки должно быть ошибкой")
	}
}

// realModsYml reproduces the exact shape of the mods.yml found inside the
// published lethal-company build, including the nested dependencies list whose
// entries are themselves YAML list items.
const realModsYml = `- manifestVersion: 1
  name: BatTeam-LethalFashion
  authorName: BatTeam
  websiteUrl: https://thunderstore.io/c/lethal-company/p/BatTeam/LethalFashion/
  displayName: LethalFashion
  description: Unlocks all of the base game suits immediately for free.
  gameVersion: "0"
  networkMode: both
  packageType: other
  installMode: managed
  installedAtTime: 1758602822076
  loaders: []
  dependencies:
    - BepInEx-BepInExPack-5.4.2100
  incompatibilities: []
  optionalDependencies: []
  versionNumber:
    major: 1
    minor: 0
    patch: 8
  enabled: true
  icon: C:\Users\Someone\AppData\Roaming\r2modmanPlus-local\icon.png
- manifestVersion: 1
  name: x753-More_Suits
  displayName: More Suits
  dependencies:
    - BepInEx-BepInExPack-5.4.2100
    - Another-Dep-1.0.0
  versionNumber:
    major: 1
    minor: 5
    patch: 4
  enabled: false
`

func TestParseProfileModsYml(t *testing.T) {
	mods, err := ParseProfile(realModsYml)
	if err != nil {
		t.Fatalf("ParseProfile: %v", err)
	}
	if len(mods) != 2 {
		t.Fatalf("получено %d модов, ожидалось 2 (вложенный dependencies не должен считаться модом)", len(mods))
	}
	if mods[0].FullName != "BatTeam-LethalFashion-1.0.8" || !mods[0].Enabled {
		t.Errorf("первый мод: %+v", mods[0])
	}
	if mods[1].FullName != "x753-More_Suits-1.5.4" || mods[1].Enabled {
		t.Errorf("второй мод: %+v", mods[1])
	}
	deps := EnabledDependencies(mods)
	if len(deps) != 1 || deps[0] != "BatTeam-LethalFashion-1.0.8" {
		t.Errorf("EnabledDependencies = %v, выключенный мод не должен попадать в сборку", deps)
	}
}

const exportR2x = `profileName: My Profile
mods:
  - name: Author-ModOne
    version:
      major: 2
      minor: 1
      patch: 0
    enabled: true
  - name: Author-ModTwo
    version:
      major: 0
      minor: 9
      patch: 12
    enabled: true
`

func TestParseProfileExportR2x(t *testing.T) {
	mods, err := ParseProfile(exportR2x)
	if err != nil {
		t.Fatalf("ParseProfile: %v", err)
	}
	if len(mods) != 2 {
		t.Fatalf("получено %d модов, ожидалось 2", len(mods))
	}
	if mods[0].FullName != "Author-ModOne-2.1.0" || mods[1].FullName != "Author-ModTwo-0.9.12" {
		t.Errorf("моды: %+v", mods)
	}
}

func TestParseProfileRejectsGarbage(t *testing.T) {
	if _, err := ParseProfile("не yaml вовсе"); err == nil {
		t.Fatal("ожидалась ошибка на документе без элементов списка")
	}
}

// fakeThunderstore serves just enough of the API for the resolver.
func fakeThunderstore(t *testing.T, versions map[string][]string) *httptest.Server {
	t.Helper()
	mux := http.NewServeMux()
	mux.HandleFunc("/api/experimental/package/", func(w http.ResponseWriter, r *http.Request) {
		trimmed := strings.Trim(strings.TrimPrefix(r.URL.Path, "/api/experimental/package/"), "/")
		parts := strings.Split(trimmed, "/")
		if len(parts) != 3 {
			http.NotFound(w, r)
			return
		}
		full := fmt.Sprintf("%s-%s-%s", parts[0], parts[1], parts[2])
		deps, ok := versions[full]
		if !ok {
			http.NotFound(w, r)
			return
		}
		_ = json.NewEncoder(w).Encode(PackageVersion{
			Namespace:     parts[0],
			Name:          parts[1],
			VersionNumber: parts[2],
			FullName:      full,
			Dependencies:  deps,
			IsActive:      true,
		})
	})
	return httptest.NewServer(mux)
}

func TestResolveFindsTransitiveLoader(t *testing.T) {
	// Regression for Enhanced_HowToFish: BepInEx is NOT among the modpack's
	// direct dependencies, it arrives through one of the mods. A resolver that
	// only looks one level deep builds a pack with no loader in it.
	srv := fakeThunderstore(t, map[string][]string{
		"Linux_Squad-Enhanced_HowToFish-1.0.5": {"evansvl-ModMenu-0.3.6", "welffi-StickyItems-1.0.17"},
		"evansvl-ModMenu-0.3.6":                {"BepInEx-BepInExPack-5.4.2305"},
		"welffi-StickyItems-1.0.17":            {},
		"BepInEx-BepInExPack-5.4.2305":         {},
	})
	defer srv.Close()

	c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	eco := &Ecosystem{ModloaderPackages: []ModloaderPackage{
		{PackageID: "BepInEx-BepInExPack", RootFolder: "BepInExPack", Loader: "bepinex"},
	}}

	res, err := c.Resolve(context.Background(), eco, "Linux_Squad-Enhanced_HowToFish-1.0.5")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if got := res.TotalPackages(); got != 4 {
		t.Errorf("в дереве %d пакетов, ожидалось 4", got)
	}
	if res.Loader != "BepInEx-BepInExPack-5.4.2305" {
		t.Errorf("загрузчик не найден: %q", res.Loader)
	}
	if len(res.Missing) != 0 {
		t.Errorf("Missing = %v, ожидался пустой", res.Missing)
	}
	var loaderSeen bool
	for _, p := range res.Packages {
		if p.IsLoader && p.LoaderRoot == "BepInExPack" {
			loaderSeen = true
		}
	}
	if !loaderSeen {
		t.Error("пакет загрузчика должен быть помечен IsLoader с корневой папкой из схемы")
	}
}

func TestResolveFirstWinsAndReportsMissing(t *testing.T) {
	srv := fakeThunderstore(t, map[string][]string{
		// Root asks for Shared 1.0.0; ModA asks for Shared 2.0.0. First wins,
		// so 2.0.0 is never fetched — matching what r2modman does, because
		// Thunderstore publishes no constraints a solver could use.
		"Root-Pack-1.0.0":  {"Lib-Shared-1.0.0", "Some-ModA-1.0.0", "Gone-Mod-9.9.9"},
		"Lib-Shared-1.0.0": {},
		"Some-ModA-1.0.0":  {"Lib-Shared-2.0.0"},
	})
	defer srv.Close()

	c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	res, err := c.Resolve(context.Background(), &Ecosystem{}, "Root-Pack-1.0.0")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	for _, p := range res.Packages {
		if p.FullName == "Lib-Shared-2.0.0" {
			t.Error("вторая версия общей библиотеки не должна попадать в дерево")
		}
	}
	if len(res.Missing) != 1 || res.Missing[0] != "Gone-Mod-9.9.9" {
		t.Errorf("Missing = %v, ожидался ровно удалённый пакет", res.Missing)
	}
	if res.TotalPackages() != 3 {
		t.Errorf("в дереве %d пакетов, ожидалось 3", res.TotalPackages())
	}
}

func TestResolveFailsHardOnServerError(t *testing.T) {
	// A timeout or a 503 must NOT be recorded as "this mod no longer exists":
	// that is exactly how a build succeeds while quietly dropping packages.
	mux := http.NewServeMux()
	mux.HandleFunc("/api/experimental/package/", func(w http.ResponseWriter, r *http.Request) {
		if strings.Contains(r.URL.Path, "/Root/Pack/") {
			_ = json.NewEncoder(w).Encode(PackageVersion{
				Namespace: "Root", Name: "Pack", VersionNumber: "1.0.0",
				FullName: "Root-Pack-1.0.0", Dependencies: []string{"Flaky-Mod-1.0.0"},
			})
			return
		}
		w.WriteHeader(http.StatusServiceUnavailable)
	})
	srv := httptest.NewServer(mux)
	defer srv.Close()

	c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	if _, err := c.Resolve(context.Background(), &Ecosystem{}, "Root-Pack-1.0.0"); err == nil {
		t.Fatal("сетевая ошибка должна валить резолв, а не превращаться в Missing")
	}
}

func TestEcosystemHelpers(t *testing.T) {
	eco := &Ecosystem{
		Games: map[string]EcoGame{
			"how-to-fish": {
				Label:         "how-to-fish",
				Distributions: nil,
				R2modman: []R2modmanDef{{
					SteamFolderName: "How to Fish/How to Fish",
					ExeNames:        []string{"How to Fish.exe"},
					PackageLoader:   "bepinex",
					Distributions:   []Distribution{{Platform: "steam", Identifier: "4001890"}},
				}},
			},
		},
		ModloaderPackages: []ModloaderPackage{{PackageID: "BepInEx-BepInExPack", RootFolder: "BepInExPack"}},
	}
	g := eco.Games["how-to-fish"]
	if got := g.SteamAppID(); got != "4001890" {
		// The game-level distributions list is empty for how-to-fish; the id
		// only exists inside the r2modman definition.
		t.Errorf("SteamAppID = %q, ожидалось 4001890", got)
	}
	def, ok := g.Def()
	if !ok || def.SteamFolderName != "How to Fish/How to Fish" {
		t.Errorf("Def = %+v, %v", def, ok)
	}
	if root, ok := eco.LoaderRoot("bepinex", "bepinexpack"); !ok || root != "BepInExPack" {
		t.Errorf("LoaderRoot без учёта регистра: %q, %v", root, ok)
	}
	if _, ok := eco.LoaderRoot("Author", "OrdinaryMod"); ok {
		t.Error("обычный мод не должен опознаваться как загрузчик")
	}
}

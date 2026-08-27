package mods

import (
	"context"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
	"time"

	"ChillHub/server/internal/adminutil"
)

// These tests talk to the real Thunderstore. They are skipped unless
// CHILLHUB_NET_TESTS=1, so CI and `make test` stay offline and deterministic,
// but the layout engine can be checked against live packages whenever the
// rules or the ecosystem schema change.
//
//	CHILLHUB_NET_TESTS=1 go test ./internal/adminapi/mods/ -run Live -v
func requireNetwork(t *testing.T) {
	t.Helper()
	if os.Getenv("CHILLHUB_NET_TESTS") != "1" {
		t.Skip("сетевой тест: CHILLHUB_NET_TESTS=1 чтобы включить")
	}
}

// TestLiveHowToFishModpack builds Linux_Squad/Enhanced_HowToFish end to end.
//
// It is the smallest real modpack on Thunderstore that still exercises
// everything that matters: a transitively pulled mod loader, per-mod plugin
// folders, a shared config directory and the junk sweep. Roughly 2 MB and 19
// packages, so it stays fast enough to run by hand.
func TestLiveHowToFishModpack(t *testing.T) {
	requireNetwork(t)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()

	client := NewClient(nil)
	ecoCache := NewEcosystemCache(client, t.TempDir())

	eco, err := ecoCache.Get(ctx)
	if err != nil {
		t.Fatalf("ecosystem schema: %v", err)
	}
	t.Logf("схема версии %s, игр: %d, пакетов-загрузчиков: %d",
		eco.SchemaVersion, len(eco.Games), len(eco.ModloaderPackages))

	def := liveHowToFishDef(t, ctx, ecoCache)

	res, err := client.Resolve(ctx, eco, "Linux_Squad-Enhanced_HowToFish-1.0.5")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	t.Logf("дерево: %d пакетов, недоступно: %d, загрузчик: %s",
		res.TotalPackages(), len(res.Missing), res.Loader)
	if len(res.Missing) != 0 {
		t.Errorf("недоступные пакеты: %v", res.Missing)
	}
	if res.TotalPackages() != 19 {
		t.Errorf("в дереве %d пакетов, ожидалось 19", res.TotalPackages())
	}
	if res.Loader == "" {
		t.Fatal("загрузчик не найден — а он приходит транзитивно, не прямой зависимостью")
	}

	layout, err := NewLayout(def)
	if err != nil {
		t.Fatalf("NewLayout: %v", err)
	}

	root := t.TempDir()
	downloaded := buildLiveTree(t, ctx, client, layout, res, root)

	removed, err := SweepJunk(root)
	if err != nil {
		t.Fatalf("SweepJunk: %v", err)
	}
	files, bytes := CountTree(root)
	t.Logf("скачано %.1f МБ, вычищено %d мусорных файлов, на выходе %d файлов / %.1f МБ",
		float64(downloaded)/(1<<20), removed, files, float64(bytes)/(1<<20))

	assertLoadableTree(t, root)
	assertNoJunkLeft(t, root)

	// The Python prototype of this exact pipeline produced 45 files; a large
	// drift means the layout rules changed meaning, not that the pack changed.
	if files < 30 || files > 70 {
		t.Errorf("на выходе %d файлов — прототип давал 45, расхождение стоит разобрать", files)
	}
}

// liveHowToFishDef fetches the game definition and guards the values the
// launcher will depend on to find the Steam copy.
func liveHowToFishDef(t *testing.T, ctx context.Context, cache *EcosystemCache) R2modmanDef {
	t.Helper()

	game, err := cache.Game(ctx, "how-to-fish")
	if err != nil {
		t.Fatalf("игра how-to-fish: %v", err)
	}
	def, ok := game.Def()
	if !ok {
		t.Fatal("у how-to-fish нет определения r2modman")
	}
	if got := game.SteamAppID(); got != "4001890" {
		t.Errorf("Steam AppID = %q, ожидалось 4001890", got)
	}
	if def.SteamFolderName != "How to Fish/How to Fish" {
		t.Errorf("steamFolderName = %q — вложенная папка, на ней ломается наивный поиск", def.SteamFolderName)
	}
	if def.PackageLoader != "bepinex" {
		t.Errorf("packageLoader = %q", def.PackageLoader)
	}
	return def
}

// buildLiveTree downloads every package of a resolution and lays it out,
// returning the number of bytes fetched.
func buildLiveTree(t *testing.T, ctx context.Context, client *Client, layout *Layout, res *Resolution, root string) int64 {
	t.Helper()

	cacheDir := t.TempDir()
	budget := adminutil.NewExtractBudget(512 << 20)
	var total int64

	for _, p := range res.Packages {
		zipPath := filepath.Join(cacheDir, p.FullName+".zip")
		f, err := os.Create(zipPath) // #nosec G304 -- test temp dir
		if err != nil {
			t.Fatal(err)
		}
		n, err := client.Download(ctx, p.Ref(), f)
		_ = f.Close()
		if err != nil {
			t.Fatalf("скачивание %s: %v", p.FullName, err)
		}
		total += n
		if _, err := layout.InstallPackage(root, p, zipPath, budget); err != nil {
			t.Fatalf("раскладка %s: %v", p.FullName, err)
		}
	}
	return total
}

// assertLoadableTree checks the files without which the game would not load a
// single mod.
func assertLoadableTree(t *testing.T, root string) {
	t.Helper()
	for _, rel := range []string{
		"winhttp.dll",
		"doorstop_config.ini",
		".doorstop_version",
		"BepInEx/core/BepInEx.Preloader.dll",
	} {
		if _, err := os.Stat(filepath.Join(root, filepath.FromSlash(rel))); err != nil {
			t.Errorf("в собранном дереве нет %s: %v", rel, err)
		}
	}
}

// assertNoJunkLeft checks that the sweep reached every level of the tree and
// that plugins actually landed somewhere.
func assertNoJunkLeft(t *testing.T, root string) {
	t.Helper()

	var junkLeft []string
	var plugins int
	_ = filepath.WalkDir(root, func(p string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		if d.IsDir() {
			return nil
		}
		rel, _ := filepath.Rel(root, p)
		if JunkNames[strings.ToLower(d.Name())] {
			junkLeft = append(junkLeft, filepath.ToSlash(rel))
		}
		if strings.HasPrefix(filepath.ToSlash(rel), "BepInEx/plugins/") {
			plugins++
		}
		return nil
	})
	sort.Strings(junkLeft)
	if len(junkLeft) != 0 {
		t.Errorf("после чистки остался мусор: %v", junkLeft)
	}
	if plugins == 0 {
		t.Error("в BepInEx/plugins не оказалось ни одного файла")
	}
}

// TestLiveLethalReloadedResolve only resolves — it does not download the 1.8 GB
// tree. It is the regression that keeps the rate limiting honest: the first
// run of this resolve without pacing lost 59 of 151 packages.
func TestLiveLethalReloadedResolve(t *testing.T) {
	requireNetwork(t)

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Minute)
	defer cancel()

	client := NewClient(nil)
	eco, err := NewEcosystemCache(client, t.TempDir()).Get(ctx)
	if err != nil {
		t.Fatalf("ecosystem schema: %v", err)
	}

	res, err := client.Resolve(ctx, eco, "ASTeam-LethalReloaded-2.2.12")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	t.Logf("дерево: %d пакетов, недоступно: %d, загрузчик: %s",
		res.TotalPackages(), len(res.Missing), res.Loader)

	if len(res.Missing) != 0 {
		t.Errorf("недоступные пакеты: %v", res.Missing)
	}
	if res.TotalPackages() != 151 {
		t.Errorf("в дереве %d пакетов, ожидался 151", res.TotalPackages())
	}
	if res.Loader == "" {
		t.Error("загрузчик не найден")
	}
}

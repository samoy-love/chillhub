package mods

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// Справочник игр Thunderstore: кеш в памяти, кеш на диске и сеть.
//
// Порядок здесь не косметика. Сборка модпака не должна падать оттого, что
// Thunderstore недоступен именно сейчас: раскладка файлов зависит от правил,
// которые меняются, когда в каталог добавляют игру, — то есть не на протяжении
// одной сборки. Поэтому копия на диске важнее свежести.

func schemaServer(t *testing.T, calls *int) *httptest.Server {
	t.Helper()
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		*calls++
		_ = json.NewEncoder(w).Encode(Ecosystem{
			SchemaVersion: "0.3.0",
			Games: map[string]EcoGame{
				"lethal-company": {
					Label:         "lethal-company",
					Distributions: []Distribution{{Platform: "steam", Identifier: "1966720"}},
					R2modman: []R2modmanDef{{
						SteamFolderName: "Lethal Company",
						PackageLoader:   "bepinex",
						InstallRules:    bepinexRules().InstallRules,
					}},
				},
			},
			ModloaderPackages: []ModloaderPackage{
				{PackageID: "BepInEx-BepInExPack", RootFolder: "BepInExPack"},
			},
		})
	}))
	t.Cleanup(srv.Close)
	return srv
}

func TestEcosystemCacheFetchesOnceAndStoresOnDisk(t *testing.T) {
	calls := 0
	srv := schemaServer(t, &calls)
	root := t.TempDir()
	client := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	cache := NewEcosystemCache(client, root)

	eco, err := cache.Get(context.Background())
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if eco.SchemaVersion != "0.3.0" || len(eco.Games) != 1 {
		t.Fatalf("схема: %+v", eco.SchemaVersion)
	}

	// Второй вызов обязан прийти из памяти.
	if _, err := cache.Get(context.Background()); err != nil {
		t.Fatal(err)
	}
	if calls != 1 {
		t.Errorf("запросов к серверу %d, ожидался один", calls)
	}

	// И лечь на диск: следующий запуск процесса не должен ходить в сеть.
	onDisk := filepath.Join(root, "tmp", "ecosystem.json")
	if _, err := os.Stat(onDisk); err != nil {
		t.Fatalf("копия на диске не создана: %v", err)
	}

	fresh := NewEcosystemCache(client, root)
	if _, err := fresh.Get(context.Background()); err != nil {
		t.Fatal(err)
	}
	if calls != 1 {
		t.Errorf("новый кеш сходил в сеть при живой копии на диске (запросов %d)", calls)
	}
}

func TestEcosystemCacheSurvivesDeadServer(t *testing.T) {
	calls := 0
	srv := schemaServer(t, &calls)
	root := t.TempDir()
	client := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)

	if _, err := NewEcosystemCache(client, root).Get(context.Background()); err != nil {
		t.Fatal(err)
	}
	srv.Close()

	// Копия на диске есть, сервер мёртв — сборка обязана продолжаться.
	// Устаревшее описание игры не может быть причиной неверной раскладки:
	// правила меняются, когда в каталог добавляют игру, а не в течение сборки.
	stale := NewEcosystemCache(client, root)
	stale.fetchedAt = time.Now().Add(-2 * ecosystemTTL)
	eco, err := stale.Get(context.Background())
	if err != nil {
		t.Fatalf("Get с мёртвым сервером: %v", err)
	}
	if len(eco.Games) != 1 {
		t.Errorf("игр в копии: %d", len(eco.Games))
	}
}

func TestEcosystemCacheFailsWithoutAnyCopy(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusInternalServerError)
	}))
	defer srv.Close()

	client := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	cache := NewEcosystemCache(client, t.TempDir())

	// Холодный старт без сети и без копии — единственный случай, когда отказ
	// честнее молчания: раскладывать файлы не по чему.
	if _, err := cache.Get(context.Background()); err == nil {
		t.Fatal("ожидалась ошибка при холодном старте без схемы")
	}
}

func TestEcosystemCacheIgnoresBrokenDiskCopy(t *testing.T) {
	calls := 0
	srv := schemaServer(t, &calls)
	root := t.TempDir()
	client := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)

	p := filepath.Join(root, "tmp", "ecosystem.json")
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(p, []byte("{это не json"), 0o644); err != nil {
		t.Fatal(err)
	}

	if _, err := NewEcosystemCache(client, root).Get(context.Background()); err != nil {
		t.Fatalf("битая копия должна игнорироваться, а не ломать сборку: %v", err)
	}
	if calls != 1 {
		t.Errorf("запросов %d — при битой копии нужен поход в сеть", calls)
	}
}

func TestEcosystemGameLookup(t *testing.T) {
	calls := 0
	srv := schemaServer(t, &calls)
	client := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	cache := NewEcosystemCache(client, t.TempDir())
	ctx := context.Background()

	game, err := cache.Game(ctx, "LETHAL-COMPANY")
	if err != nil {
		t.Fatalf("поиск игры регистронезависим: %v", err)
	}
	if game.SteamAppID() != "1966720" {
		t.Errorf("AppID %q", game.SteamAppID())
	}
	if _, err := cache.Game(ctx, "нет-такой-игры"); err == nil {
		t.Error("ожидалась ошибка на неизвестной игре")
	}
}

func TestCountTreeSumsFilesAndBytes(t *testing.T) {
	root := t.TempDir()
	if err := os.MkdirAll(filepath.Join(root, "BepInEx", "plugins"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "winhttp.dll"), []byte("12345"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(root, "BepInEx", "plugins", "Mod.dll"), []byte("123"), 0o644); err != nil {
		t.Fatal(err)
	}

	files, bytesTotal := CountTree(root)
	if files != 2 || bytesTotal != 8 {
		t.Errorf("CountTree = (%d, %d), ожидалось (2, 8)", files, bytesTotal)
	}

	// Несуществующее дерево — нули, а не паника: отчёт о сборке не должен
	// зависеть от того, успели ли уже создать каталог.
	if f, b := CountTree(filepath.Join(root, "нет")); f != 0 || b != 0 {
		t.Errorf("CountTree несуществующего = (%d, %d)", f, b)
	}
}

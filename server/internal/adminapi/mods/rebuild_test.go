package mods

import (
	"encoding/json"
	"net/url"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

// Пересборка уже опубликованной версии и то, ради чего она вообще нужна.
//
// Состав модпака меняет его автор — для этого есть «Собрать N». Раскладку
// меняем мы, и версия, собранная старыми правилами, остаётся лежать собранной
// по-старому, пока её не тронешь: ни по имени версии, ни по дате в панели
// этого не видно.

// seedTwoLoaderPack — модпак, который старый конвейер разложил бы с двумя
// загрузчиками. Здесь он нужен целиком: пересборка обязана дать то же, что даёт
// обычная сборка сегодня.
func seedTwoLoaderPack(fs *fakeStore) {
	fs.add("Team-Pack-1.0.0", []string{"bbepis-BepInExPack-5.4.2121", "Author-CoolMod-1.0.0"}, map[string]string{
		"manifest.json": `{"name":"Pack"}`,
	})
	fs.add("Author-CoolMod-1.0.0", []string{"BepInEx-BepInExPack-5.4.2305"}, map[string]string{
		"CoolMod.dll": "mod code",
	})
	fs.add("bbepis-BepInExPack-5.4.2121", nil, map[string]string{
		"BepInExPack/BepInEx/core/BepInEx.Preloader.dll": "ядро 5.4.21",
	})
	fs.add("BepInEx-BepInExPack-5.4.2305", nil, map[string]string{
		"BepInExPack/BepInEx/core/BepInEx.Preloader.dll": "ядро 5.4.23",
	})
}

func TestRebuildLaysTheTreeOutAgainUnderTheSameName(t *testing.T) {
	// Пересборка нужна, когда изменились ПРАВИЛА раскладки, а не содержимое
	// пакетов: версии на Thunderstore неизменны, и архивы берутся из кеша. Так
	// что проверяется здесь именно это — дерево строится заново, с нуля, и
	// публикуется поверх прежнего, а не рядом с ним.
	fs := newFakeStore(t)
	seedTwoLoaderPack(fs)
	h, root := testHandlers(t, fs)

	rec := doForm(t, h.Build, url.Values{
		"gameId": {"lethal-company"}, "namespace": {"Team"}, "name": {"Pack"}, "version": {"1.0.0"},
	})
	if rec.Code != 200 || strings.Contains(rec.Body.String(), `type":"error"`) {
		t.Fatalf("сборка не прошла: %d %s", rec.Code, rec.Body.String())
	}

	files := filepath.Join(root, "content", "_mods", "lethal-company", "Team-Pack-1.0.0", "files")
	mod := filepath.Join(files, "BepInEx", "plugins", "Author-CoolMod", "CoolMod.dll")
	if err := os.Remove(mod); err != nil {
		t.Fatalf("портим опубликованное дерево: %v", err)
	}

	rec = doForm(t, h.Rebuild, url.Values{"gameId": {"lethal-company"}, "version": {"Team-Pack-1.0.0"}})
	if rec.Code != 200 || strings.Contains(rec.Body.String(), `type":"error"`) {
		t.Fatalf("пересборка не прошла: %d %s", rec.Code, rec.Body.String())
	}

	if _, err := os.Stat(mod); err != nil {
		t.Errorf("после пересборки файла мода нет: %v", err)
	}
	// Загрузчик по-прежнему один и тот, что назвал сам модпак.
	core, err := os.ReadFile(filepath.Join(files, "BepInEx", "core", "BepInEx.Preloader.dll")) // #nosec G304
	if err != nil {
		t.Fatalf("читаем ядро: %v", err)
	}
	if string(core) != "ядро 5.4.21" {
		t.Errorf("в ядре %q — пересборка разложила не тот загрузчик", core)
	}

	// Пересборка публикует поверх себя, а не плодит соседей, которые потом
	// ждут активации по одному.
	versions, err := os.ReadDir(filepath.Join(root, "content", "_mods", "lethal-company"))
	if err != nil {
		t.Fatal(err)
	}
	if len(versions) != 1 || versions[0].Name() != "Team-Pack-1.0.0" {
		t.Errorf("в каталоге версий %v, ожидалась одна и та же", versions)
	}
}

func TestRebuildKeepsTheDigestOfAnUnchangedTree(t *testing.T) {
	// Отпечаток — это то, по чему лаунчер понимает, что дерево другое. Он
	// обязан молчать о пересборке, ничего не изменившей: иначе каждая позовёт
	// всех игроков «обновиться» впустую, и на это перестанут смотреть.
	fs := newFakeStore(t)
	seedTwoLoaderPack(fs)
	h, _ := testHandlers(t, fs)

	rec := doForm(t, h.Build, url.Values{
		"gameId": {"lethal-company"}, "namespace": {"Team"}, "name": {"Pack"}, "version": {"1.0.0"},
	})
	if rec.Code != 200 {
		t.Fatalf("сборка не прошла: %s", rec.Body.String())
	}
	first, err := h.builder.ReadSource("lethal-company", "Team-Pack-1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	if first.TreeDigest == "" {
		t.Fatal("отпечаток дерева не записан — лаунчеру нечего сравнивать")
	}

	rec = doForm(t, h.Rebuild, url.Values{"gameId": {"lethal-company"}, "version": {"Team-Pack-1.0.0"}})
	if rec.Code != 200 {
		t.Fatalf("пересборка не прошла: %s", rec.Body.String())
	}
	again, err := h.builder.ReadSource("lethal-company", "Team-Pack-1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	if again.TreeDigest != first.TreeDigest {
		t.Errorf("отпечаток изменился без изменений в дереве: %s -> %s", first.TreeDigest, again.TreeDigest)
	}
}

func TestRebuildRecordsWhatItWasResolvedFrom(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	rec := doForm(t, h.Build, url.Values{
		"gameId": {"lethal-company"}, "namespace": {"Team"}, "name": {"Pack"}, "version": {"1.0.0"},
	})
	if rec.Code != 200 {
		t.Fatalf("сборка не прошла: %s", rec.Body.String())
	}
	src, err := h.builder.ReadSource("lethal-company", "Team-Pack-1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	if !slices.Equal(src.Roots, []string{"Team-Pack-1.0.0"}) {
		t.Errorf("Roots = %v, без них импорт профиля нечем пересобрать", src.Roots)
	}
}

func TestRebuildRefusesAnImportWhoseCompositionWasNotRecorded(t *testing.T) {
	// Импорт профиля r2modman, сделанный до появления Roots: имя версии своё,
	// и что в неё входило, не знает никто. Собрать «по имени» тут значило бы
	// собрать не то и опубликовать под тем же номером — хуже, чем отказаться.
	fs := newFakeStore(t)
	seedPack(fs)
	h, root := testHandlers(t, fs)

	dir := filepath.Join(root, "manifests", "_mods", "lethal-company", "sources")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	old, err := json.Marshal(Source{Kind: SourceProfile, Version: "lethal-1.0.7", DisplayName: "Импорт"})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "lethal-1.0.7.json"), old, 0o644); err != nil {
		t.Fatal(err)
	}

	rec := doForm(t, h.Rebuild, url.Values{"gameId": {"lethal-company"}, "version": {"lethal-1.0.7"}})
	if rec.Code != 400 {
		t.Fatalf("код %d, ожидался отказ", rec.Code)
	}
	if !strings.Contains(rec.Body.String(), "Импорт") && !strings.Contains(rec.Body.String(), "профиля") {
		t.Errorf("отказ не объясняет, что делать: %q", rec.Body.String())
	}
}

func TestRebuildRefusesAVersionThatWasNeverBuilt(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	rec := doForm(t, h.Rebuild, url.Values{"gameId": {"lethal-company"}, "version": {"Nobody-Pack-1.0.0"}})
	if rec.Code != 400 {
		t.Errorf("код %d, ожидался отказ на несобранную версию", rec.Code)
	}
}

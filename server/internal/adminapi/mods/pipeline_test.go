package mods

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// Тесты конвейера сборки: что она берёт из сети, в каком порядке кладёт файлы
// и что рассказывает о себе, пока это делает.

// TestBuildFallsBackToPackageDownloadURL охраняет пакет, на котором сборка
// однажды встала целиком.
//
// Имя объекта в хранилище выводится из полного имени пакета — но только пока
// имя короткое. Длинное Thunderstore обрезает и приписывает случайный хвост, и
// угаданный адрес отвечает 403 AccessDenied от самого бакета, а не 404. Своя
// ссылка пакета из метаданных знает правильный адрес всегда.
func TestBuildFallsBackToPackageDownloadURL(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.cdnDenied["Author-CoolMod-1.0.0"] = true

	b, root := testBuilder(t, fs)
	if _, err := b.Build(context.Background(), thunderstoreRequest(), false, nil); err != nil {
		t.Fatalf("сборка не пережила 403 на угаданном адресе: %v", err)
	}
	if fs.dlHits["Author-CoolMod-1.0.0"] == 0 {
		t.Error("запасной адрес пакета не использован")
	}
	// Файл мода обязан оказаться в дереве: запасной путь должен давать тот же
	// архив, а не просто не падать.
	assertFileExists(t, root, "BepInEx/plugins/Author-CoolMod/CoolMod.dll")
}

// TestArchiveSizeFallsBackToo: оценка размера ходит по тем же адресам. Если бы
// запасной путь был только у скачивания, сборка большого пака сначала
// недосчиталась бы гигабайта в прогнозе места.
func TestArchiveSizeFallsBackToo(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.cdnDenied["Author-CoolMod-1.0.0"] = true

	b, _ := testBuilder(t, fs)
	plan, err := b.Resolve(context.Background(), thunderstoreRequest())
	if err != nil {
		t.Fatal(err)
	}
	if plan.TotalBytes == 0 {
		t.Fatal("размер пака посчитан нулевым")
	}
	if fs.dlHits["Author-CoolMod-1.0.0"] == 0 {
		t.Error("оценка размера не пошла на запасной адрес")
	}
}

// TestBuildSurvivesFlakyArchiveAndReportsRetries: обрыв на архиве повторяется
// внутри сборки, и повтор виден в потоке событий.
//
// Раньше повтор был, но молчал: в логе сервера, которого оператор не видит.
// На экране это выглядело как замершая полоса.
func TestBuildSurvivesFlakyArchiveAndReportsRetries(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.failCDNTimes["Author-CoolMod-1.0.0"] = 2

	b, root := testBuilder(t, fs)
	var retries []Event
	_, err := b.Build(context.Background(), thunderstoreRequest(), false, func(e Event) {
		if e.Type == "retry" {
			retries = append(retries, e)
		}
	})
	if err != nil {
		t.Fatalf("две неудачи подряд уронили сборку: %v", err)
	}
	if len(retries) != 2 {
		t.Fatalf("о повторах сообщено %d раз, ожидалось 2: %+v", len(retries), retries)
	}
	if !strings.Contains(retries[0].Message, "Author-CoolMod") {
		t.Errorf("событие повтора не называет пакет: %q", retries[0].Message)
	}
	assertFileExists(t, root, "BepInEx/plugins/Author-CoolMod/CoolMod.dll")
}

// TestBuildInstallsInPlanOrder: скачивание идёт в несколько потоков, установка
// — строго по плану.
//
// Порядок здесь не косметика. Два пакета могут положить файл по одному пути, и
// решать, чей останется, обязано место в дереве зависимостей, а не то, чей
// распаковщик успел первым. Событие «package» отмечает именно установку,
// поэтому его номера обязаны идти подряд.
func TestBuildInstallsInPlanOrder(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	// Ещё десяток пакетов, чтобы работникам было где обогнать друг друга.
	deps := []string{"Author-CoolMod-1.0.0"}
	for i := 0; i < 12; i++ {
		full := "Filler-Mod" + string(rune('A'+i)) + "-1.0.0"
		fs.add(full, nil, map[string]string{"Mod.dll": "code " + full})
		deps = append(deps, full)
	}
	fs.deps["Team-Pack-1.0.0"] = deps

	b, _ := testBuilder(t, fs)
	var steps []int
	var names []string
	if _, err := b.Build(context.Background(), thunderstoreRequest(), false, func(e Event) {
		if e.Type == "package" {
			steps = append(steps, e.Step)
			names = append(names, e.Message)
		}
	}); err != nil {
		t.Fatal(err)
	}
	if len(steps) != 15 {
		t.Fatalf("установлено %d пакетов из 15", len(steps))
	}
	for i, s := range steps {
		if s != i+1 {
			t.Fatalf("установка вышла из порядка на %d-м событии: Step=%d, пакеты %v", i+1, s, names)
		}
	}
}

// TestResolveWalksNestedModpacks: модпак в зависимостях модпака.
//
// Обход не различает «модпак» и «мод» — на Thunderstore это одна и та же
// сущность, — поэтому вложенность обязана разворачиваться сама, на любую
// глубину. Тест держит это свойство: пак → пак → пак → мод, четыре уровня.
func TestResolveWalksNestedModpacks(t *testing.T) {
	fs := newFakeStore(t)
	fs.add("Top-Pack-1.0.0", []string{"Mid-Pack-1.0.0"}, map[string]string{"manifest.json": "{}"})
	fs.add("Mid-Pack-1.0.0", []string{"Low-Pack-1.0.0", "Author-CoolMod-1.0.0"}, map[string]string{"manifest.json": "{}"})
	fs.add("Low-Pack-1.0.0", []string{"Deep-Mod-1.0.0"}, map[string]string{"manifest.json": "{}"})
	fs.add("Author-CoolMod-1.0.0", nil, map[string]string{"CoolMod.dll": "code"})
	fs.add("Deep-Mod-1.0.0", []string{"BepInEx-BepInExPack-5.4.2305"}, map[string]string{"Deep.dll": "code"})
	fs.add("BepInEx-BepInExPack-5.4.2305", nil, map[string]string{"BepInExPack/winhttp.dll": "loader"})

	b, _ := testBuilder(t, fs)
	res, err := b.Client.Resolve(context.Background(), mustEco(t, b), "Top-Pack-1.0.0")
	if err != nil {
		t.Fatal(err)
	}
	got := map[string]bool{}
	for _, p := range res.Packages {
		got[p.FullName] = true
	}
	for _, want := range []string{
		"Top-Pack-1.0.0", "Mid-Pack-1.0.0", "Low-Pack-1.0.0",
		"Author-CoolMod-1.0.0", "Deep-Mod-1.0.0", "BepInEx-BepInExPack-5.4.2305",
	} {
		if !got[want] {
			t.Errorf("вложенный обход не дошёл до %s: собрано %v", want, res.Packages)
		}
	}
	// Загрузчик найден через три вложенных пака — именно он делает пак
	// работающим, и потерять его значит собрать игру без модов.
	if res.Loader != "BepInEx-BepInExPack-5.4.2305" {
		t.Errorf("загрузчик не опознан: %q", res.Loader)
	}
}

// TestMetadataCacheSkipsSecondWalk: версия на Thunderstore неизменна, значит
// её описание можно не спрашивать дважды.
//
// Без этого пересборка пака после обновления одного мода снова стоит 151
// запрос и минуту выдержки перед первым скачанным байтом.
func TestMetadataCacheSkipsSecondWalk(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	b, _ := testBuilder(t, fs)

	if _, err := b.Resolve(context.Background(), thunderstoreRequest()); err != nil {
		t.Fatal(err)
	}
	first := fs.total(fs.apiHits)
	if first == 0 {
		t.Fatal("первый обход не сходил в сеть — тест ничего не проверяет")
	}

	if _, err := b.Resolve(context.Background(), thunderstoreRequest()); err != nil {
		t.Fatal(err)
	}
	if second := fs.total(fs.apiHits); second != first {
		t.Errorf("повторный обход сходил в сеть ещё %d раз", second-first)
	}
}

// assertFileExists проверяет, что файл лежит в опубликованном дереве версии.
func assertFileExists(t *testing.T, root, rel string) {
	t.Helper()
	p := filepath.Join(root, "content", "_mods", "lethal-company", "Team-Pack-1.0.0", "files",
		filepath.FromSlash(rel))
	if _, err := os.Stat(p); err != nil {
		t.Errorf("в дереве нет %s: %v", rel, err)
	}
}

// mustEco достаёт схему из прогретого кеша сборщика.
func mustEco(t *testing.T, b *Builder) *Ecosystem {
	t.Helper()
	eco, err := b.Eco.Get(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	return eco
}

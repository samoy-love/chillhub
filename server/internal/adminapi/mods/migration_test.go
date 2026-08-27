package mods

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"sort"
	"strings"
	"testing"
	"time"
)

// Сверка миграции: совпадает ли модпак, собранный из mods.yml опубликованной
// сборки, с тем набором модов, который у игроков уже стоит.
//
// Каждая существующая модовая сборка была собрана вручную в r2modman, и внутри
// неё лежит экспорт профиля — mods.yml с точными версиями. Переход на
// раздельную раздачу обязан сохранить состав ДОСЛОВНО: игрок не должен
// заметить, что игра теперь собирается иначе.
//
// Проверка идёт без скачивания архивов. Имена папок в BepInEx/plugins — это и
// есть идентификаторы пакетов ({Author}-{ModName}), которые туда положил
// r2modman, а значит опубликованный манифест сам по себе является эталонным
// списком установленного. Сравнивать с ним разрешённое нами дерево — ровно тот
// шаг «сверить», который план требует сделать ДО выкатки, а не по жалобам.
//
//	CHILLHUB_NET_TESTS=1 go test ./internal/adminapi/mods/ -run Migration -v

const (
	// prodBase is the public API of the live server.
	prodBase = "https://launcher.samoy.love"

	// migrationGame/migrationVersion name the build being migrated.
	migrationGame    = "lethal-company"
	migrationVersion = "1.0.7"
)

func TestLiveMigrationLethalCompany(t *testing.T) {
	requireNetwork(t)

	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Minute)
	defer cancel()

	installed := installedPackagesFromManifest(t, ctx, migrationGame, migrationVersion)
	t.Logf("в опубликованной сборке %s %s: %d папок модов в BepInEx/plugins",
		migrationGame, migrationVersion, len(installed))
	if len(installed) == 0 {
		t.Fatal("в сборке нет ни одной папки мода — сверять нечего")
	}

	profile := fetchText(t, ctx, fmt.Sprintf("%s/content/%s/%s/files/mods.yml", prodBase, migrationGame, migrationVersion))
	mods, err := ParseProfile(profile)
	if err != nil {
		t.Fatalf("разбор mods.yml: %v", err)
	}
	enabled := EnabledDependencies(mods)
	t.Logf("в mods.yml: %d модов, включено %d", len(mods), len(enabled))

	client := NewClient(nil)
	eco, err := NewEcosystemCache(client, t.TempDir()).Get(ctx)
	if err != nil {
		t.Fatalf("ecosystem schema: %v", err)
	}

	res, err := client.ResolveList(ctx, eco, enabled)
	if err != nil {
		t.Fatalf("резолв дерева: %v", err)
	}
	t.Logf("дерево из профиля: %d пакетов, недоступно %d, загрузчик %s",
		res.TotalPackages(), len(res.Missing), res.Loader)

	if len(res.Missing) != 0 {
		// Пакет, которого больше нет на Thunderstore, — это не «мелкая
		// потеря»: игроки с ним играют прямо сейчас, и после миграции он
		// исчезнет у всех разом.
		t.Errorf("НЕ ВОСПРОИЗВОДИТСЯ: %d пакетов больше нет на Thunderstore: %v",
			len(res.Missing), res.Missing)
	}
	if res.Loader == "" {
		t.Error("в дереве нет пакета загрузчика — собранный модпак не запустит моды")
	}

	// Загрузчик в папке plugins не лежит, поэтому из сравнения он исключён.
	resolved := map[string]bool{}
	for _, p := range res.Packages {
		if p.IsLoader {
			continue
		}
		resolved[PackageKey(p.Namespace, p.Name)] = true
	}

	var missingFromResolve, extraInResolve []string
	for key, folder := range installed {
		if !resolved[key] {
			missingFromResolve = append(missingFromResolve, folder)
		}
	}
	for key := range resolved {
		if _, had := installed[key]; !had {
			extraInResolve = append(extraInResolve, key)
		}
	}
	sort.Strings(missingFromResolve)
	sort.Strings(extraInResolve)

	// Пропавшее — настоящая регрессия: мод стоит у игроков, а собранный пак его
	// не содержит.
	if len(missingFromResolve) > 0 {
		t.Errorf("в собранном модпаке НЕ БУДЕТ %d модов, которые стоят сейчас: %v",
			len(missingFromResolve), head(missingFromResolve, 20))
	}

	// Лишнее — не ошибка сама по себе: это библиотеки, которые r2modman ставит
	// не в plugins (patchers, core), и зависимости, добавившиеся у модов с
	// момента ручной сборки. Но список стоит увидеть глазами перед выкаткой.
	if len(extraInResolve) > 0 {
		t.Logf("в модпаке появится %d пакетов сверх папок plugins (библиотеки и новые зависимости): %v",
			len(extraInResolve), head(extraInResolve, 20))
	}

	t.Logf("ИТОГ: совпало %d из %d установленных модов",
		len(installed)-len(missingFromResolve), len(installed))
}

// installedPackagesFromManifest читает опубликованный манифест сборки и
// возвращает идентификаторы модов по именам папок в BepInEx/plugins.
func installedPackagesFromManifest(t *testing.T, ctx context.Context, gid, version string) map[string]string {
	t.Helper()

	body := fetchText(t, ctx, fmt.Sprintf("%s/manifests/%s/%s.json", prodBase, gid, version))
	var m struct {
		Files []struct {
			Path string `json:"path"`
		} `json:"files"`
	}
	if err := json.Unmarshal([]byte(body), &m); err != nil {
		t.Fatalf("разбор манифеста: %v", err)
	}

	const prefix = "BepInEx/plugins/"
	out := map[string]string{}
	for _, f := range m.Files {
		p := strings.ReplaceAll(f.Path, "\\", "/")
		if !strings.HasPrefix(p, prefix) {
			continue
		}
		rest := strings.TrimPrefix(p, prefix)
		slash := strings.Index(rest, "/")
		if slash <= 0 {
			// Файл лежит прямо в plugins, без папки мода: так r2modman не
			// раскладывает, это ручная правка. В сверке не участвует.
			continue
		}
		folder := rest[:slash]
		dash := strings.Index(folder, "-")
		if dash <= 0 {
			continue
		}
		out[PackageKey(folder[:dash], folder[dash+1:])] = folder
	}
	return out
}

// fetchText скачивает текстовый файл, проваливая тест на любой сбой.
func fetchText(t *testing.T, ctx context.Context, url string) string {
	t.Helper()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, url, nil)
	if err != nil {
		t.Fatalf("запрос %s: %v", url, err)
	}
	req.Header.Set("User-Agent", userAgent)

	res, err := http.DefaultClient.Do(req)
	if err != nil {
		t.Fatalf("GET %s: %v", url, err)
	}
	defer func() { _ = res.Body.Close() }()

	if res.StatusCode != http.StatusOK {
		t.Fatalf("GET %s: статус %d", url, res.StatusCode)
	}
	body, err := io.ReadAll(io.LimitReader(res.Body, 64<<20))
	if err != nil {
		t.Fatalf("чтение %s: %v", url, err)
	}
	return string(body)
}

func head(list []string, n int) []string {
	if len(list) <= n {
		return list
	}
	return append(append([]string{}, list[:n]...), fmt.Sprintf("… и ещё %d", len(list)-n))
}

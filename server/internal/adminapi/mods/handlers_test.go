package mods

import (
	"encoding/json"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"

	"ChillHub/server/internal/adminapi/games"
)

// Эндпоинты вкладки «Моды».
//
// Проверяется прежде всего то, что НЕ должно случиться: чужой или выключенный
// gameId не открывает работу с модами, длинная сборка отдаёт поток событий, а
// не молчание, и активная версия не удаляется. Всё это ошибки, которые в
// админке выглядят как «кнопка не сработала», и разбираются только по логам.

// testHandlers поднимает эндпоинты над временным контентом с готовым реестром.
func testHandlers(t *testing.T, fs *fakeStore) (*Handlers, string) {
	t.Helper()
	b, root := testBuilder(t, fs)

	registry := `{"items":[
      {"gameId":"lethal-company","title":"Lethal Company","order":0,
       "mods":{"enabled":true,"community":"lethal-company","ecosystemGame":"lethal-company",
               "loader":"bepinex","steamAppId":"1966720","steamFolder":"Lethal Company"}},
      {"gameId":"off-game","title":"Моды выключены","order":1,
       "mods":{"enabled":false,"community":"lethal-company"}},
      {"gameId":"plain-game","title":"Без модов","order":2}
    ]}`
	p := filepath.Join(root, "manifests", "_registry", "games.json")
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(p, []byte(registry), 0o644); err != nil {
		t.Fatal(err)
	}

	h := &Handlers{builder: b, games: games.New(root), builds: b.Builds}
	return h, root
}

func doForm(t *testing.T, fn http.HandlerFunc, values url.Values) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequest(http.MethodPost, "/admin/api/mods/x", strings.NewReader(values.Encode()))
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	rec := httptest.NewRecorder()
	fn(rec, req)
	return rec
}

func doGet(t *testing.T, fn http.HandlerFunc, query string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequest(http.MethodGet, "/admin/api/mods/x?"+query, nil)
	rec := httptest.NewRecorder()
	fn(rec, req)
	return rec
}

func TestHandlersRefuseGamesWithoutMods(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	// Игра без настроек модов и игра с выключенными модами одинаково не
	// открывают работу с модпаками: иначе оператор собрал бы пак игре, у
	// которой моды выключены намеренно.
	for _, gid := range []string{"plain-game", "off-game"} {
		rec := doGet(t, h.List, "gameId="+gid)
		if rec.Code != http.StatusBadRequest {
			t.Errorf("List(%s) = %d, ожидался 400", gid, rec.Code)
		}
	}
	if rec := doGet(t, h.List, "gameId=нет-такой"); rec.Code != http.StatusBadRequest {
		t.Errorf("несуществующий gameId дал %d", rec.Code)
	}
	if rec := doGet(t, h.List, "gameId=../etc"); rec.Code != http.StatusBadRequest {
		t.Errorf("небезопасный gameId дал %d, а он становится путём", rec.Code)
	}
}

func TestListReportsBuiltVersions(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	rec := doGet(t, h.List, "gameId=lethal-company")
	if rec.Code != http.StatusOK {
		t.Fatalf("List = %d: %s", rec.Code, rec.Body.String())
	}
	var empty struct {
		Items  []VersionInfo `json:"items"`
		Active string        `json:"active"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &empty); err != nil {
		t.Fatal(err)
	}
	if len(empty.Items) != 0 || empty.Active != "" {
		t.Errorf("до сборки версий быть не должно: %+v", empty)
	}

	// Собираем и активируем — список обязан это показать.
	if rec := doForm(t, h.Build, url.Values{
		"gameId": {"lethal-company"}, "namespace": {"Team"}, "name": {"Pack"}, "version": {"1.0.0"},
	}); rec.Code != http.StatusOK {
		t.Fatalf("Build = %d: %s", rec.Code, rec.Body.String())
	}
	if rec := doForm(t, h.Activate, url.Values{
		"gameId": {"lethal-company"}, "version": {"Team-Pack-1.0.0"},
	}); rec.Code != http.StatusOK {
		t.Fatalf("Activate = %d: %s", rec.Code, rec.Body.String())
	}

	rec = doGet(t, h.List, "gameId=lethal-company")
	var got struct {
		Items  []VersionInfo `json:"items"`
		Active string        `json:"active"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &got); err != nil {
		t.Fatal(err)
	}
	if len(got.Items) != 1 || !got.Items[0].Active {
		t.Fatalf("версии: %+v", got.Items)
	}
	if got.Active != "Team-Pack-1.0.0" || got.Items[0].Packages != 3 {
		t.Errorf("активная %q, пакетов %d", got.Active, got.Items[0].Packages)
	}
}

func TestBuildStreamsProgressAsNdjson(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	rec := doForm(t, h.Build, url.Values{
		"gameId": {"lethal-company"}, "namespace": {"Team"}, "name": {"Pack"}, "version": {"1.0.0"},
	})
	if ct := rec.Header().Get("Content-Type"); ct != "application/x-ndjson" {
		t.Errorf("Content-Type = %q", ct)
	}

	// Сборка идёт минутами: ответ обязан быть потоком событий, иначе она
	// неотличима от зависшей.
	var types []string
	for line := range strings.SplitSeq(strings.TrimSpace(rec.Body.String()), "\n") {
		var ev Event
		if json.Unmarshal([]byte(line), &ev) == nil {
			types = append(types, ev.Type)
		}
	}
	for _, want := range []string{"start", "resolved", "package", "done"} {
		if !containsString(types, want) {
			t.Errorf("в потоке нет события %q: %v", want, types)
		}
	}
}

func TestBuildAcceptsPackageLink(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	// Половина модпаков не помечена категорией и в каталоге не находится —
	// вставленная ссылка должна работать наравне с выбором из каталога.
	rec := doForm(t, h.Resolve, url.Values{
		"gameId":     {"lethal-company"},
		"packageUrl": {"https://thunderstore.io/c/lethal-company/p/Team/Pack/"},
		"version":    {"1.0.0"},
	})
	if rec.Code != http.StatusOK {
		t.Fatalf("Resolve = %d: %s", rec.Code, rec.Body.String())
	}
	var plan struct {
		Version    string `json:"version"`
		Packages   int    `json:"packages"`
		PackageURL string `json:"packageUrl"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &plan); err != nil {
		t.Fatal(err)
	}
	if plan.Version != "Team-Pack-1.0.0" || plan.Packages != 3 {
		t.Errorf("план: %+v", plan)
	}
	if !strings.Contains(plan.PackageURL, "/p/Team/Pack/") {
		t.Errorf("ссылка на страницу пакета: %q", plan.PackageURL)
	}
}

func TestResolveRejectsGarbageLink(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	rec := doForm(t, h.Resolve, url.Values{
		"gameId": {"lethal-company"}, "packageUrl": {"https://example.com/что-то"},
	})
	if rec.Code != http.StatusBadRequest {
		t.Errorf("мусорная ссылка дала %d", rec.Code)
	}
	rec = doForm(t, h.Resolve, url.Values{"gameId": {"lethal-company"}})
	if rec.Code != http.StatusBadRequest {
		t.Errorf("запрос без модпака дал %d", rec.Code)
	}
}

func TestActivateAndDeleteGuardTheActiveVersion(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	if rec := doForm(t, h.Build, url.Values{
		"gameId": {"lethal-company"}, "namespace": {"Team"}, "name": {"Pack"}, "version": {"1.0.0"},
	}); rec.Code != http.StatusOK {
		t.Fatal(rec.Body.String())
	}

	// Активировать можно только собранное.
	if rec := doForm(t, h.Activate, url.Values{
		"gameId": {"lethal-company"}, "version": {"Нет-Такой-1.0.0"},
	}); rec.Code != http.StatusBadRequest {
		t.Errorf("активация несуществующей версии дала %d", rec.Code)
	}

	if rec := doForm(t, h.Activate, url.Values{
		"gameId": {"lethal-company"}, "version": {"Team-Pack-1.0.0"},
	}); rec.Code != http.StatusOK {
		t.Fatal(rec.Body.String())
	}

	// Удаление активной версии оставило бы каждый лаунчер с манифестом,
	// которого больше нет.
	if rec := doForm(t, h.DeleteVersion, url.Values{
		"gameId": {"lethal-company"}, "version": {"Team-Pack-1.0.0"},
	}); rec.Code != http.StatusBadRequest {
		t.Errorf("удаление активной версии дало %d", rec.Code)
	}
}

func TestDiffComparesTwoBuiltVersions(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	fs.add("Author-CoolMod-1.1.0", []string{"BepInEx-BepInExPack-5.4.2305"}, map[string]string{"CoolMod.dll": "v2"})
	fs.add("Team-Pack-1.1.0", []string{"Author-CoolMod-1.1.0"}, map[string]string{"manifest.json": "{}"})

	for _, v := range []string{"1.0.0", "1.1.0"} {
		if rec := doForm(t, h.Build, url.Values{
			"gameId": {"lethal-company"}, "namespace": {"Team"}, "name": {"Pack"}, "version": {v},
		}); rec.Code != http.StatusOK {
			t.Fatalf("сборка %s: %s", v, rec.Body.String())
		}
	}

	rec := doGet(t, h.Diff, "gameId=lethal-company&from=Team-Pack-1.0.0&to=Team-Pack-1.1.0")
	if rec.Code != http.StatusOK {
		t.Fatalf("Diff = %d: %s", rec.Code, rec.Body.String())
	}
	var out struct {
		Items []DiffEntry `json:"items"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	var updated bool
	for _, d := range out.Items {
		if d.Package == "Author-CoolMod" && d.Change == "updated" && d.To == "1.1.0" {
			updated = true
		}
	}
	if !updated {
		t.Errorf("в диффе нет обновления мода: %+v", out.Items)
	}

	// Версия без записи состава — внятный отказ, а не пустой дифф.
	if rec := doGet(t, h.Diff, "gameId=lethal-company&from=Нет-1.0.0&to=Team-Pack-1.1.0"); rec.Code != http.StatusBadRequest {
		t.Errorf("дифф с несуществующей версией дал %d", rec.Code)
	}
}

func TestImportBuildsFromProfile(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	profile := "- manifestVersion: 1\n" +
		"  name: Author-CoolMod\n" +
		"  versionNumber:\n    major: 1\n    minor: 0\n    patch: 0\n  enabled: true\n"

	body, contentType := multipartProfile(t, url.Values{
		"gameId": {"lethal-company"}, "version": {"import-1.0.7"},
	}, profile)

	req := httptest.NewRequest(http.MethodPost, "/admin/api/mods/import", strings.NewReader(body))
	req.Header.Set("Content-Type", contentType)
	rec := httptest.NewRecorder()
	h.Import(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("Import = %d: %s", rec.Code, rec.Body.String())
	}
	if !strings.Contains(rec.Body.String(), `"done"`) {
		t.Errorf("поток импорта без события done: %s", rec.Body.String())
	}

	src, err := h.builder.ReadSource("lethal-company", "import-1.0.7")
	if err != nil {
		t.Fatalf("ReadSource: %v", err)
	}
	if src.Kind != SourceProfile || len(src.Tree) != 2 {
		t.Errorf("состав импорта: %+v", src)
	}
}

func TestImportRejectsBadInput(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	// Имя версии становится именем каталога — небезопасное отвергается до
	// того, как начнётся что-либо длительное.
	body, ct := multipartProfile(t, url.Values{
		"gameId": {"lethal-company"}, "version": {"../побег"},
	}, "- manifestVersion: 1\n  name: A-B\n  versionNumber:\n    major: 1\n    minor: 0\n    patch: 0\n")
	req := httptest.NewRequest(http.MethodPost, "/admin/api/mods/import", strings.NewReader(body))
	req.Header.Set("Content-Type", ct)
	rec := httptest.NewRecorder()
	h.Import(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Errorf("небезопасное имя версии дало %d", rec.Code)
	}

	// Файл, который не является профилем, обязан отвергаться обычным 400, а не
	// потоком, умирающим на первом событии.
	body, ct = multipartProfile(t, url.Values{
		"gameId": {"lethal-company"}, "version": {"import-1"},
	}, "это не профиль r2modman")
	req = httptest.NewRequest(http.MethodPost, "/admin/api/mods/import", strings.NewReader(body))
	req.Header.Set("Content-Type", ct)
	rec = httptest.NewRecorder()
	h.Import(rec, req)
	if rec.Code != http.StatusBadRequest {
		t.Errorf("не-профиль дал %d: %s", rec.Code, rec.Body.String())
	}
}

func TestCacheEndpointReportsAndSweeps(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	if rec := doForm(t, h.Build, url.Values{
		"gameId": {"lethal-company"}, "namespace": {"Team"}, "name": {"Pack"}, "version": {"1.0.0"},
	}); rec.Code != http.StatusOK {
		t.Fatal(rec.Body.String())
	}

	rec := doGet(t, h.Cache, "")
	var stats struct {
		Files   int   `json:"files"`
		Bytes   int64 `json:"bytes"`
		TTLDays int   `json:"ttlDays"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &stats); err != nil {
		t.Fatal(err)
	}
	if stats.Files != 3 || stats.Bytes == 0 || stats.TTLDays != 30 {
		t.Errorf("состояние кеша: %+v", stats)
	}

	// Подметание по TTL свежие архивы не трогает.
	if rec := doForm(t, h.Cache, url.Values{}); rec.Code != http.StatusOK {
		t.Fatalf("Cache POST = %d", rec.Code)
	}
	if files, _ := h.builder.Cache.Stats(); files != 3 {
		t.Errorf("после подметания в кеше %d файлов", files)
	}

	// Полная очистка удаляет всё.
	if rec := doForm(t, h.Cache, url.Values{"all": {"1"}}); rec.Code != http.StatusOK {
		t.Fatalf("Cache clear = %d", rec.Code)
	}
	if files, _ := h.builder.Cache.Stats(); files != 0 {
		t.Errorf("после очистки в кеше %d файлов", files)
	}
}

func TestHandlersRejectWrongMethod(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	get := func(fn http.HandlerFunc) int {
		req := httptest.NewRequest(http.MethodGet, "/x", nil)
		rec := httptest.NewRecorder()
		fn(rec, req)
		return rec.Code
	}
	for name, fn := range map[string]http.HandlerFunc{
		"Build": h.Build, "Resolve": h.Resolve, "Activate": h.Activate,
		"DeleteVersion": h.DeleteVersion, "Import": h.Import, "Ecosystem": h.Ecosystem,
	} {
		if code := get(fn); code != http.StatusMethodNotAllowed {
			t.Errorf("%s на GET = %d, ожидался 405", name, code)
		}
	}
}

// multipartProfile собирает тело multipart с текстовыми полями и профилем.
func multipartProfile(t *testing.T, fields url.Values, profile string) (body, contentType string) {
	t.Helper()
	var sb strings.Builder
	w := multipart.NewWriter(&sb)
	for k, vs := range fields {
		for _, v := range vs {
			if err := w.WriteField(k, v); err != nil {
				t.Fatal(err)
			}
		}
	}
	part, err := w.CreateFormFile("file", "mods.yml")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := part.Write([]byte(profile)); err != nil {
		t.Fatal(err)
	}
	if err := w.Close(); err != nil {
		t.Fatal(err)
	}
	return sb.String(), w.FormDataContentType()
}

func containsString(list []string, want string) bool {
	return slices.Contains(list, want)
}

func TestCatalogHandlerFiltersBySection(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	rec := doGet(t, h.Catalog, "gameId=lethal-company&q=Pack&ordering=top-rated&page=2")
	if rec.Code != http.StatusOK {
		t.Fatalf("Catalog = %d: %s", rec.Code, rec.Body.String())
	}
	var out struct {
		Count     int            `json:"count"`
		Results   []CatalogEntry `json:"results"`
		BrowseURL string         `json:"browseUrl"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	if out.Count != 1 || len(out.Results) != 1 {
		t.Errorf("каталог: count=%d, результатов=%d", out.Count, len(out.Results))
	}
	// Раздел «Модпаки» адресуется UUID: со слагом сайт молча покажет весь
	// каталог, и оператор будет искать пак среди тысяч пакетов.
	if !strings.Contains(fs.lastListing, "section=018bb887") {
		t.Errorf("запрос к каталогу без UUID раздела: %s", fs.lastListing)
	}
	if !strings.Contains(out.BrowseURL, "section=018bb887") {
		t.Errorf("ссылка на сайт без UUID раздела: %s", out.BrowseURL)
	}
}

func TestReadmeHandlerResolvesLatestVersion(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	rec := doGet(t, h.Readme, "namespace=Team&name=Pack&version=1.0.0")
	if rec.Code != http.StatusOK {
		t.Fatalf("Readme = %d: %s", rec.Code, rec.Body.String())
	}
	var out struct {
		Markdown string `json:"markdown"`
		Version  string `json:"version"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(out.Markdown, "Pack") || out.Version != "1.0.0" {
		t.Errorf("README: %+v", out)
	}

	// Пакета нет — внятный отказ, а не пустая карточка.
	if rec := doGet(t, h.Readme, "namespace=Нет&name=Такого&version=1.0.0"); rec.Code == http.StatusOK {
		t.Errorf("README несуществующего пакета вернул 200")
	}
}

func TestEcosystemHandlerFillsGameSettings(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, root := testHandlers(t, fs)

	rec := doForm(t, h.Ecosystem, url.Values{
		"gameId": {"plain-game"}, "slug": {"lethal-company"},
	})
	if rec.Code != http.StatusOK {
		t.Fatalf("Ecosystem = %d: %s", rec.Code, rec.Body.String())
	}

	// Настройки должны лечь в реестр, а не только вернуться в ответе: панель
	// сохраняет реестр целиком, и потерянное здесь поле пропало бы молча.
	entry, ok := games.New(root).Entry("plain-game")
	if !ok || entry.Mods == nil {
		t.Fatalf("запись реестра: %+v", entry)
	}
	if !entry.Mods.Enabled || entry.Mods.Community != "lethal-company" {
		t.Errorf("настройки модов: %+v", entry.Mods)
	}
	if entry.Mods.SectionUUID == "" {
		t.Error("UUID раздела не сохранён — ссылка на каталог будет без фильтра")
	}
	if entry.Mods.Loader != "bepinex" {
		t.Errorf("загрузчик %q", entry.Mods.Loader)
	}
}

func TestEcosystemHandlerRejectsBadInput(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	cases := []url.Values{
		{"gameId": {"../etc"}, "slug": {"lethal-company"}},
		{"gameId": {"plain-game"}, "slug": {"Lethal Company"}},
		{"gameId": {"нет-такой"}, "slug": {"lethal-company"}},
	}
	for _, v := range cases {
		if rec := doForm(t, h.Ecosystem, v); rec.Code == http.StatusOK {
			t.Errorf("Ecosystem принял %v", v)
		}
	}

	// Игры нет в схеме Thunderstore — это отказ шлюза, а не 200 с пустыми
	// настройками.
	if rec := doForm(t, h.Ecosystem, url.Values{
		"gameId": {"plain-game"}, "slug": {"неизвестная-игра"},
	}); rec.Code == http.StatusOK {
		t.Error("Ecosystem принял игру, которой нет в схеме")
	}
}

func TestBuilderAccessorIsWired(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	h, _ := testHandlers(t, fs)

	// Builder() отдаёт тот же конвейер, что используют обработчики: на нём
	// висит подметание кеша по расписанию в main.
	if h.Builder() != h.builder {
		t.Error("Builder() отдаёт не тот конвейер")
	}
	if h.Builder().Cache == nil || h.Builder().Eco == nil {
		t.Error("конвейер собран не полностью")
	}
}

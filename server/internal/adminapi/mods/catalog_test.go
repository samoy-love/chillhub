package mods

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// Каталог Thunderstore: адресация раздела и разбор ссылок.
//
// Главное, что здесь проверяется, — раздел «Модпаки» адресуется UUID, СВОИМ
// у каждой игры. Слаг сайт принимает и молча игнорирует, возвращая весь
// каталог целиком: ошибка выглядит как «фильтр не работает», а не как отказ,
// и заметить её по коду ответа невозможно.

// fakeCatalog отдаёт /filters/ и /listing/, запоминая параметры запросов.
type fakeCatalog struct {
	*httptest.Server

	lastQuery map[string]string
	sections  []Section
	count     int
}

func newFakeCatalog(t *testing.T, sections []Section) *fakeCatalog {
	t.Helper()
	fc := &fakeCatalog{lastQuery: map[string]string{}, sections: sections, count: 42}

	mux := http.NewServeMux()
	mux.HandleFunc("/api/cyberstorm/community/", func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasSuffix(r.URL.Path, "/filters/") {
			http.NotFound(w, r)
			return
		}
		_ = json.NewEncoder(w).Encode(filtersDoc{Sections: fc.sections})
	})
	mux.HandleFunc("/api/cyberstorm/listing/", func(w http.ResponseWriter, r *http.Request) {
		for k, v := range r.URL.Query() {
			fc.lastQuery[k] = v[0]
		}
		_ = json.NewEncoder(w).Encode(CatalogPage{
			Count: fc.count,
			Results: []CatalogEntry{{
				Namespace: "ASTeam", Name: "LethalReloaded", Downloads: 51818,
			}},
		})
	})

	fc.Server = httptest.NewServer(mux)
	t.Cleanup(fc.Close)
	// Кеш разделов — пакетная переменная; между тестами его надо сбрасывать,
	// иначе второй тест увидит разделы первого.
	t.Cleanup(resetSectionCache)
	resetSectionCache()
	return fc
}

// resetSectionCache очищает пакетный кеш разделов: он один на процесс и
// ключуется только слагом, поэтому без сброса второй тест увидит разделы,
// положенные первым.
func resetSectionCache() {
	sections.mu.Lock()
	defer sections.mu.Unlock()
	sections.byID = map[string][]Section{}
	sections.at = map[string]time.Time{}
}

func catalogClient(fc *fakeCatalog) *Client {
	return NewClient(fc.Client()).WithBases(fc.URL, fc.URL+"/cdn").WithInterval(time.Millisecond)
}

var modpackSections = []Section{
	{UUID: "018bb887-fa45-e515-cfea-76e144826dbc", Name: "Mods", Slug: "mods"},
	{UUID: "018bb887-fa52-7236-0344-e714696ee5d5", Name: "Modpacks", Slug: "modpacks"},
}

func TestModpacksSectionUUID(t *testing.T) {
	fc := newFakeCatalog(t, modpackSections)
	c := catalogClient(fc)

	uuid, err := c.ModpacksSectionUUID(context.Background(), "lethal-company")
	if err != nil {
		t.Fatalf("ModpacksSectionUUID: %v", err)
	}
	if uuid != "018bb887-fa52-7236-0344-e714696ee5d5" {
		t.Errorf("uuid = %q", uuid)
	}
}

func TestModpacksSectionMissing(t *testing.T) {
	// У сообщества без раздела модпаков ошибка должна быть внятной, а не
	// пустой строкой, по которой каталог тихо покажет всё подряд.
	fc := newFakeCatalog(t, []Section{{UUID: "x", Slug: "mods"}})
	c := catalogClient(fc)

	if _, err := c.ModpacksSectionUUID(context.Background(), "lethal-company"); err == nil {
		t.Fatal("ожидалась ошибка про отсутствующий раздел")
	}
}

func TestSectionsAreCached(t *testing.T) {
	fc := newFakeCatalog(t, modpackSections)
	c := catalogClient(fc)
	ctx := context.Background()

	if _, err := c.Sections(ctx, "lethal-company"); err != nil {
		t.Fatal(err)
	}
	// Меняем ответ сервера: второй вызов обязан прийти из кеша.
	fc.sections = nil
	list, err := c.Sections(ctx, "lethal-company")
	if err != nil {
		t.Fatal(err)
	}
	if len(list) != 2 {
		t.Errorf("разделов %d, кеш не сработал", len(list))
	}
}

func TestCatalogPassesSectionAndOrdering(t *testing.T) {
	fc := newFakeCatalog(t, modpackSections)
	c := catalogClient(fc)

	page, err := c.Catalog(context.Background(), "lethal-company",
		"018bb887-fa52-7236-0344-e714696ee5d5", " LethalReloaded ", "top-rated", 3)
	if err != nil {
		t.Fatalf("Catalog: %v", err)
	}
	if page.Count != 42 || len(page.Results) != 1 {
		t.Errorf("страница: count=%d, результатов=%d", page.Count, len(page.Results))
	}
	if got := fc.lastQuery["section"]; got != "018bb887-fa52-7236-0344-e714696ee5d5" {
		t.Errorf("section = %q", got)
	}
	if got := fc.lastQuery["ordering"]; got != "top-rated" {
		t.Errorf("ordering = %q", got)
	}
	if got := fc.lastQuery["page"]; got != "3" {
		t.Errorf("page = %q", got)
	}
	if got := fc.lastQuery["q"]; got != "LethalReloaded" {
		t.Errorf("q = %q — пробелы должны обрезаться", got)
	}
}

func TestCatalogNormalizesBadArguments(t *testing.T) {
	fc := newFakeCatalog(t, modpackSections)
	c := catalogClient(fc)

	// Неизвестная сортировка — это 400 от API и пустой каталог без объяснения;
	// подменяем её на осмысленную по умолчанию.
	if _, err := c.Catalog(context.Background(), "lethal-company", "", "", "по-моему-так", -5); err != nil {
		t.Fatalf("Catalog: %v", err)
	}
	if got := fc.lastQuery["ordering"]; got != "most-downloaded" {
		t.Errorf("ordering = %q, ожидалась подстановка по умолчанию", got)
	}
	if got := fc.lastQuery["page"]; got != "1" {
		t.Errorf("page = %q, отрицательная страница должна стать первой", got)
	}
	if _, seen := fc.lastQuery["q"]; seen {
		t.Error("пустой поиск не должен уходить параметром")
	}
}

func TestCatalogRejectsUnusableCommunity(t *testing.T) {
	fc := newFakeCatalog(t, modpackSections)
	c := catalogClient(fc)

	for _, bad := range []string{"", "Lethal Company", "../etc", "UPPER", strings.Repeat("x", 200)} {
		if _, err := c.Catalog(context.Background(), bad, "", "", "", 1); err == nil {
			t.Errorf("слаг %q принят, а он становится частью пути URL", bad)
		}
		if _, err := c.Sections(context.Background(), bad); err == nil {
			t.Errorf("Sections принял слаг %q", bad)
		}
	}
}

func TestBrowseURLUsesSectionUUID(t *testing.T) {
	fc := newFakeCatalog(t, modpackSections)
	c := catalogClient(fc)

	got := c.BrowseURL(context.Background(), "lethal-company")
	// Ссылка ведёт человека на сайт: со слагом вместо UUID сайт покажет весь
	// каталог, и оператор будет искать модпак среди 38 тысяч пакетов.
	if !strings.Contains(got, "section=018bb887-fa52-7236-0344-e714696ee5d5") {
		t.Errorf("BrowseURL = %q, в ней нет UUID раздела", got)
	}
	if !strings.Contains(got, "ordering=most-downloaded") {
		t.Errorf("BrowseURL = %q, нет сортировки", got)
	}
}

func TestBrowseURLWithoutSectionStillWorks(t *testing.T) {
	// Раздела нет или /filters/ недоступен — ссылка всё равно должна вести на
	// каталог игры, просто без фильтра.
	fc := newFakeCatalog(t, nil)
	c := catalogClient(fc)

	got := c.BrowseURL(context.Background(), "how-to-fish")
	if !strings.HasPrefix(got, "https://thunderstore.io/c/how-to-fish/") {
		t.Errorf("BrowseURL = %q", got)
	}
	if strings.Contains(got, "section=") {
		t.Errorf("BrowseURL = %q, раздела нет — параметра быть не должно", got)
	}
}

func TestParsePackageURL(t *testing.T) {
	cases := []struct {
		raw                 string
		community, ns, name string
		ok                  bool
	}{
		{"https://thunderstore.io/c/lethal-company/p/ASTeam/LethalReloaded/", "lethal-company", "ASTeam", "LethalReloaded", true},
		// Без завершающего слеша и с http — операторы вставляют что угодно.
		{"http://thunderstore.io/c/how-to-fish/p/Linux_Squad/Enhanced_HowToFish", "how-to-fish", "Linux_Squad", "Enhanced_HowToFish", true},
		// Старая форма ссылки: поисковики до сих пор отдают именно её.
		{"https://thunderstore.io/package/bbepis/BepInExPack/", "", "bbepis", "BepInExPack", true},
		{"  https://thunderstore.io/c/repo/p/A/B/  ", "repo", "A", "B", true},
		{"https://example.com/c/x/p/A/B/", "", "", "", false},
		{"не ссылка вовсе", "", "", "", false},
		{"", "", "", "", false},
	}
	for _, c := range cases {
		community, ns, name, ok := ParsePackageURL(c.raw)
		if ok != c.ok || community != c.community || ns != c.ns || name != c.name {
			t.Errorf("ParsePackageURL(%q) = (%q,%q,%q,%v), ожидалось (%q,%q,%q,%v)",
				c.raw, community, ns, name, ok, c.community, c.ns, c.name, c.ok)
		}
	}
}

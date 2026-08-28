package mods

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// communityListing is the shape of /c/{slug}/api/v1/package/, cut to the fields
// that matter: two packages, one of them owned by a team whose name contains a
// hyphen.
const communityListing = `[
  {"owner":"Team","name":"Pack","full_name":"Team-Pack","versions":[
    {"namespace":"Team","name":"Pack","full_name":"Team-Pack-1.0.0","version_number":"1.0.0",
     "dependencies":["swuff-star-ConfigurableCrafting-1.0.0"],
     "download_url":"https://example.invalid/Team-Pack-1.0.0.zip","file_size":1024}
  ]},
  {"owner":"swuff-star","name":"ConfigurableCrafting","full_name":"swuff-star-ConfigurableCrafting","versions":[
    {"namespace":"swuff-star","name":"ConfigurableCrafting","full_name":"swuff-star-ConfigurableCrafting-1.0.0",
     "version_number":"1.0.0","dependencies":[],
     "download_url":"https://example.invalid/swuff.zip","file_size":2048}
  ]}
]`

// indexServer serves the listing and counts how many times it was asked for.
func indexServer(t *testing.T, body string, status int) (*Client, *int) {
	t.Helper()
	hits := 0
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasSuffix(r.URL.Path, "/api/v1/package/") {
			http.NotFound(w, r)
			return
		}
		hits++
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		_, _ = w.Write([]byte(body))
	}))
	t.Cleanup(srv.Close)
	return NewClient(nil).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond), &hits
}

func TestCommunityIndexHoldsEveryVersion(t *testing.T) {
	c, hits := indexServer(t, communityListing, http.StatusOK)

	idx, err := c.FetchCommunityIndex(context.Background(), "lethal-company")
	if err != nil {
		t.Fatal(err)
	}

	if idx.Len() != 2 {
		t.Fatalf("в индексе %d версий, ожидалось 2", idx.Len())
	}
	if *hits != 1 {
		t.Errorf("список сообщества запрошен %d раз вместо одного", *hits)
	}
	if idx.Community() != "lethal-company" {
		t.Errorf("индекс не помнит своё сообщество: %q", idx.Community())
	}

	v, ok := idx.Lookup("Team-Pack-1.0.0")
	if !ok {
		t.Fatal("пакет не нашёлся по полному имени версии")
	}
	if v.FileSize != 1024 {
		t.Errorf("размер архива %d, ожидался 1024", v.FileSize)
	}
	if v.DownloadURL == "" {
		t.Error("адрес архива потерян — на нём держится обход 403 у длинных имён")
	}
	if len(v.Dependencies) != 1 {
		t.Errorf("зависимости потеряны: %v", v.Dependencies)
	}
}

// ПРОСТРАНСТВО ИМЁН С ДЕФИСОМ — ТО, РАДИ ЧЕГО ИНДЕКС НУЖЕН НЕ ТОЛЬКО ДЛЯ
// СКОРОСТИ.
//
// «swuff-star-ConfigurableCrafting-1.0.0» принадлежит команде swuff-star, и
// разрезать эту строку по первому дефису неоткуда знать. В индексе она найдена
// целиком, а пространство имён и имя лежат отдельными полями.
func TestCommunityIndexResolvesHyphenatedNamespace(t *testing.T) {
	c, _ := indexServer(t, communityListing, http.StatusOK)

	idx, err := c.FetchCommunityIndex(context.Background(), "lethal-company")
	if err != nil {
		t.Fatal(err)
	}

	v, ok := idx.Lookup("swuff-star-ConfigurableCrafting-1.0.0")
	if !ok {
		t.Fatal("пакет команды с дефисом в имени не нашёлся")
	}
	if v.Namespace != "swuff-star" || v.Name != "ConfigurableCrafting" {
		t.Errorf("имя разобрано как %q / %q", v.Namespace, v.Name)
	}
}

func TestCommunityIndexLookupIsCaseInsensitiveAndSafeOnNil(t *testing.T) {
	c, _ := indexServer(t, communityListing, http.StatusOK)
	idx, err := c.FetchCommunityIndex(context.Background(), "lethal-company")
	if err != nil {
		t.Fatal(err)
	}

	if _, ok := idx.Lookup("team-pack-1.0.0"); !ok {
		t.Error("поиск чувствителен к регистру, хотя имена приходят в разном")
	}

	// Индекс необязателен: без него всё работает как раньше, и вызывающий не
	// обязан это проверять.
	var missing *CommunityIndex
	if _, ok := missing.Lookup("Team-Pack-1.0.0"); ok {
		t.Error("пустой индекс что-то нашёл")
	}
	if missing.Len() != 0 || missing.Community() != "" {
		t.Error("пустой индекс отвечает не как пустой")
	}
}

func TestCommunityIndexRefusesBadSlugAndStatus(t *testing.T) {
	c, _ := indexServer(t, communityListing, http.StatusOK)
	if _, err := c.FetchCommunityIndex(context.Background(), "../etc"); err == nil {
		t.Error("небезопасный слаг принят")
	}

	bad, _ := indexServer(t, "nonsense", http.StatusOK)
	if _, err := bad.FetchCommunityIndex(context.Background(), "lethal-company"); err == nil {
		t.Error("мусор вместо JSON принят за индекс")
	}

	down, _ := indexServer(t, "", http.StatusInternalServerError)
	if _, err := down.FetchCommunityIndex(context.Background(), "lethal-company"); err == nil {
		t.Error("ответ 500 принят за индекс")
	}
}

// РАЗРЕЗ ИМЕНИ — ПЕРЕБОР, А НЕ ДОГАДКА.
func TestSplitCandidatesCoversEveryReading(t *testing.T) {
	got := SplitCandidates("swuff-star-ConfigurableCrafting-1.0.0")

	if len(got) != 2 {
		t.Fatalf("вариантов разреза %d: %+v", len(got), got)
	}
	// Самый частый случай идёт первым: пространство имён — первый сегмент.
	if got[0].Namespace != "swuff" || got[0].Name != "star-ConfigurableCrafting" {
		t.Errorf("первый вариант %+v", got[0])
	}
	// А правильный для этого пакета — второй.
	if got[1].Namespace != "swuff-star" || got[1].Name != "ConfigurableCrafting" {
		t.Errorf("второй вариант %+v", got[1])
	}
	for _, c := range got {
		if c.Version != "1.0.0" {
			t.Errorf("версия разобрана как %q — она всегда последний сегмент", c.Version)
		}
	}

	if SplitCandidates("Author-Mod") != nil {
		t.Error("строка без версии принята")
	}
	if SplitCandidates("") != nil {
		t.Error("пустая строка принята")
	}
}

// GetDependency перебирает разрезы, пока сервер отвечает 404, и сдаётся только
// когда кончились все. Прежний код останавливался на первом и объявлял пакет
// исчезнувшим — то есть предлагал собрать модпак без модов, которые на месте.
func TestGetDependencyTriesEveryReadingBeforeGivingUp(t *testing.T) {
	asked := []string{}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		asked = append(asked, r.URL.Path)
		if strings.Contains(r.URL.Path, "/swuff-star/ConfigurableCrafting/") {
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(`{"namespace":"swuff-star","name":"ConfigurableCrafting",
				"version_number":"1.0.0","full_name":"swuff-star-ConfigurableCrafting-1.0.0"}`))
			return
		}
		http.NotFound(w, r)
	}))
	defer srv.Close()
	c := NewClient(nil).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)

	v, err := c.GetDependency(context.Background(), "swuff-star-ConfigurableCrafting-1.0.0")
	if err != nil {
		t.Fatalf("пакет объявлен исчезнувшим, хотя он на месте: %v", err)
	}
	if v.Namespace != "swuff-star" {
		t.Errorf("вернулся не тот пакет: %+v", v)
	}
	if len(asked) < 2 {
		t.Errorf("перебора не было, запросов всего %d", len(asked))
	}
}

func TestGetDependencyReportsTrulyMissingPackage(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.NotFound(w, r)
	}))
	defer srv.Close()
	c := NewClient(nil).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)

	_, err := c.GetDependency(context.Background(), "Gone-Forever-1.0.0")
	if !errors.Is(err, ErrNotFound) {
		t.Errorf("исчезнувший пакет дал ошибку %v, ожидался ErrNotFound", err)
	}
}

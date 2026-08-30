package mods

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"sync"
	"testing"
	"time"
)

// Эти проверки описывают одну разобранную поломку.
//
// Модпак Eclipsed_Shores для risk-of-rain-2 был разложен этим конвейером и,
// для сверки, настоящим r2modman. Папки разошлись ровно на два пакета, и оба
// лишних были у нас: второй BepInEx поверх первого и мод, переехавший в другое
// пространство имён. Первый переписал BepInEx/core чужой версией, второй лёг
// вторым плагином с тем же GUID.

// resolveEnv is a Thunderstore stand-in that serves both the community listing
// and the per-package API, and remembers which packages were asked for one by
// one. That last part is the point of half the tests here: the fix is not
// «пакет не попал в дерево», it is «за пакетом даже не пошли».
type resolveEnv struct {
	client *Client
	mu     sync.Mutex
	asked  map[string]int
}

func (e *resolveEnv) askedFor(dep string) int {
	e.mu.Lock()
	defer e.mu.Unlock()
	return e.asked[dep]
}

// newResolveEnv serves versions through the API and community through the v1
// listing. A package may appear in one, the other, or both — which is exactly
// the distinction under test.
func newResolveEnv(t *testing.T, versions map[string][]string, community string) *resolveEnv {
	t.Helper()
	env := &resolveEnv{asked: make(map[string]int)}

	mux := http.NewServeMux()
	mux.HandleFunc("/api/experimental/package/", func(w http.ResponseWriter, r *http.Request) {
		trimmed := strings.Trim(strings.TrimPrefix(r.URL.Path, "/api/experimental/package/"), "/")
		parts := strings.Split(trimmed, "/")
		if len(parts) != 3 {
			http.NotFound(w, r)
			return
		}
		full := fmt.Sprintf("%s-%s-%s", parts[0], parts[1], parts[2])
		env.mu.Lock()
		env.asked[full]++
		env.mu.Unlock()
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
	mux.HandleFunc("/c/", func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasSuffix(r.URL.Path, "/api/v1/package/") {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(community))
	})

	srv := httptest.NewServer(mux)
	t.Cleanup(srv.Close)
	env.client = NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	return env
}

// listing builds a v1 community listing for the named versions, taking their
// dependencies from the same table the API serves. What a package publishes
// must not depend on which of the two answered.
func listing(versions map[string][]string, fulls ...string) string {
	var b strings.Builder
	b.WriteString("[")
	for i, full := range fulls {
		ns, name, ver, ok := SplitDependency(full)
		if !ok {
			panic("listing: " + full)
		}
		deps, err := json.Marshal(versions[full])
		if err != nil {
			panic(err)
		}
		if i > 0 {
			b.WriteString(",")
		}
		fmt.Fprintf(&b, `{"owner":%q,"name":%q,"full_name":"%s-%s","versions":[`+
			`{"namespace":%q,"name":%q,"full_name":%q,"version_number":%q,`+
			`"dependencies":%s,"download_url":"https://example.invalid/x.zip","file_size":1}]}`,
			ns, name, ns, name, ns, name, full, ver, deps)
	}
	b.WriteString("]")
	return b.String()
}

// twoLoaderEco is a schema in which both BepInEx packs are mod loaders — which
// is what the live schema says: bbepis-BepInExPack and BepInEx-BepInExPack are
// separate entries, both unpacking BepInExPack/ into the root of the game.
func twoLoaderEco() *Ecosystem {
	return &Ecosystem{ModloaderPackages: []ModloaderPackage{
		{PackageID: "bbepis-BepInExPack", RootFolder: "BepInExPack", Loader: "bepinex"},
		{PackageID: "BepInEx-BepInExPack", RootFolder: "BepInExPack", Loader: "bepinex"},
	}}
}

func TestResolveLaysOutExactlyOneLoader(t *testing.T) {
	// Загрузчик распаковывается в корень игры. Второй переписывает
	// BepInEx/core первого, и чей это будет core — решает порядок обхода.
	env := newResolveEnv(t, map[string][]string{
		"Smxrez-Eclipsed_Shores-9.5.0": {"bbepis-BepInExPack-5.4.2121", "tsuyoikenko-Cadet-1.0.0"},
		"bbepis-BepInExPack-5.4.2121":  {},
		"tsuyoikenko-Cadet-1.0.0":      {"BepInEx-BepInExPack-5.4.2304"},
		"BepInEx-BepInExPack-5.4.2304": {},
	}, "[]")

	res, err := env.client.Resolve(context.Background(), twoLoaderEco(), "Smxrez-Eclipsed_Shores-9.5.0")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}

	var loaders []string
	for _, p := range res.Packages {
		if p.IsLoader {
			loaders = append(loaders, p.FullName)
		}
		if p.FullName == "BepInEx-BepInExPack-5.4.2304" {
			t.Error("второй загрузчик не должен попадать в дерево вовсе: он ляжет поверх первого")
		}
	}
	if len(loaders) != 1 || loaders[0] != "bbepis-BepInExPack-5.4.2121" {
		t.Errorf("загрузчиков в дереве %v, ожидался ровно ближайший к модпаку", loaders)
	}
	if res.Loader != "bbepis-BepInExPack-5.4.2121" {
		t.Errorf("Loader = %q", res.Loader)
	}
	if !slices.Equal(res.ExtraLoaders, []string{"BepInEx-BepInExPack-5.4.2304"}) {
		t.Errorf("ExtraLoaders = %v, пропущенный загрузчик обязан быть назван", res.ExtraLoaders)
	}
	if res.TotalPackages() != 3 {
		t.Errorf("в дереве %d пакетов, ожидалось 3", res.TotalPackages())
	}
}

func TestResolveLeavesOutWhatTheCommunityDoesNotPublish(t *testing.T) {
	// tsuyoikenko-MinuanoDriver до сих пор просит rob_gaming-Driver, хотя мод
	// давно переехал в public_ParticleSystem и в списке пакетов риск-оф-рейна
	// его больше нет. r2modman такую строку не разрешает; общий API отдаёт её
	// с радостью — и в папке оказываются два Driver с одним GUID.
	versions := map[string][]string{
		"Smxrez-Eclipsed_Shores-9.5.0": {
			"bbepis-BepInExPack-5.4.2121",
			"tsuyoikenko-MinuanoDriver-1.0.2",
			"public_ParticleSystem-Driver-2.3.5",
		},
		"bbepis-BepInExPack-5.4.2121":        {},
		"tsuyoikenko-MinuanoDriver-1.0.2":    {"rob_gaming-Driver-1.6.4"},
		"public_ParticleSystem-Driver-2.3.5": {},
		// Живёт в другом сообществе, но общему API это безразлично.
		"rob_gaming-Driver-1.6.4": {},
	}
	env := newResolveEnv(t, versions, listing(versions,
		"Smxrez-Eclipsed_Shores-9.5.0",
		"bbepis-BepInExPack-5.4.2121",
		"tsuyoikenko-MinuanoDriver-1.0.2",
		"public_ParticleSystem-Driver-2.3.5",
	))

	idx, err := env.client.FetchCommunityIndex(context.Background(), "riskofrain2")
	if err != nil {
		t.Fatalf("FetchCommunityIndex: %v", err)
	}
	res, err := env.client.ResolveListWithIndex(
		context.Background(), twoLoaderEco(), []string{"Smxrez-Eclipsed_Shores-9.5.0"}, nil, idx)
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}

	for _, p := range res.Packages {
		if p.FullName == "rob_gaming-Driver-1.6.4" {
			t.Error("переехавший мод не должен ложиться вторым экземпляром рядом с новым")
		}
	}
	if !slices.Equal(res.Foreign, []string{"rob_gaming-Driver-1.6.4"}) {
		t.Errorf("Foreign = %v, пропущенный пакет обязан быть назван", res.Foreign)
	}
	if n := env.askedFor("rob_gaming-Driver-1.6.4"); n != 0 {
		t.Errorf("за пакетом чужого сообщества ходили в API %d раз(а), не нужно ни одного", n)
	}
	if res.TotalPackages() != 4 {
		t.Errorf("в дереве %d пакетов, ожидалось 4", res.TotalPackages())
	}
}

func TestResolveStillAsksTheAPIForAVersionTheIndexLacks(t *testing.T) {
	// Граница правила: сообщество пакет издаёт, но именно этой версии в
	// листинге нет. Это прежний случай — спросить общий API и, если версии
	// действительно не осталось, записать её в Missing. Отбрасывать по имени с
	// версией значило бы выкидывать живые моды пачками.
	versions := map[string][]string{
		"Root-Pack-1.0.0":  {"Lib-Shared-1.0.0"},
		"Lib-Shared-1.0.0": {},
	}
	env := newResolveEnv(t, versions, listing(versions, "Root-Pack-1.0.0", "Lib-Shared-2.0.0"))

	idx, err := env.client.FetchCommunityIndex(context.Background(), "riskofrain2")
	if err != nil {
		t.Fatalf("FetchCommunityIndex: %v", err)
	}
	res, err := env.client.ResolveListWithIndex(
		context.Background(), &Ecosystem{}, []string{"Root-Pack-1.0.0"}, nil, idx)
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}

	if len(res.Foreign) != 0 {
		t.Errorf("Foreign = %v, старая версия изданного пакета — не чужой пакет", res.Foreign)
	}
	if env.askedFor("Lib-Shared-1.0.0") == 0 {
		t.Error("за версией, которой нет в индексе, полагается сходить в API")
	}
	if res.TotalPackages() != 2 {
		t.Errorf("в дереве %d пакетов, ожидалось 2", res.TotalPackages())
	}
}

func TestResolveWithoutIndexKeepsAskingTheAPI(t *testing.T) {
	// Индекс сообщества не обязателен: если он не скачался, сборка идёт как
	// раньше. Правило «чего сообщество не издаёт, того нет» на этом пути
	// применить не к чему, и молча урезать дерево оно не должно.
	env := newResolveEnv(t, map[string][]string{
		"Root-Pack-1.0.0":         {"rob_gaming-Driver-1.6.4"},
		"rob_gaming-Driver-1.6.4": {},
	}, "[]")

	res, err := env.client.Resolve(context.Background(), &Ecosystem{}, "Root-Pack-1.0.0")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if res.TotalPackages() != 2 {
		t.Errorf("в дереве %d пакетов, ожидалось 2", res.TotalPackages())
	}
	if len(res.Foreign) != 0 {
		t.Errorf("Foreign = %v, без индекса отбрасывать нечем", res.Foreign)
	}
}

func TestCommunityIndexServes(t *testing.T) {
	c, _ := indexServer(t, communityListing, http.StatusOK)
	idx, err := c.FetchCommunityIndex(context.Background(), "lethal-company")
	if err != nil {
		t.Fatalf("FetchCommunityIndex: %v", err)
	}

	cases := []struct {
		dep  string
		want bool
	}{
		// Версия другая — пакет тот же.
		{"Team-Pack-9.9.9", true},
		// Дефис в пространстве имён режется только по последнему сегменту.
		{"swuff-star-ConfigurableCrafting-1.0.0", true},
		{"SWUFF-STAR-configurablecrafting-1.0.0", true},
		{"rob_gaming-Driver-1.6.4", false},
		{"Team-Pack", false},
		{"", false},
	}
	for _, c := range cases {
		if got := idx.Serves(c.dep); got != c.want {
			t.Errorf("Serves(%q) = %v, ожидалось %v", c.dep, got, c.want)
		}
	}

	var nilIdx *CommunityIndex
	if nilIdx.Serves("Team-Pack-1.0.0") {
		t.Error("пустой индекс не издаёт ничего")
	}
}

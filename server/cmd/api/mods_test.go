package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"

	"ChillHub/server/internal/adminapi/games"
)

// writeFile is a small helper: every fixture below is one small JSON file.
func writeFile(t *testing.T, path, body string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(body), 0o644); err != nil {
		t.Fatal(err)
	}
}

// modsFixture lays out a content root with one modded game, one plain game and
// one game whose mods are configured but not yet built.
func modsFixture(t *testing.T) string {
	t.Helper()
	root := withContentRoot(t)

	writeFile(t, filepath.Join(root, "manifests", "_registry", "games.json"), `{"items":[
      {"gameId":"lethal-company","title":"Lethal Company","exeRelativePath":"Lethal Company.exe","order":0,
       "mods":{"enabled":true,"community":"lethal-company","ecosystemGame":"lethal-company","loader":"bepinex",
               "steamAppId":"1966720","steamFolder":"Lethal Company","exeNames":["Lethal Company.exe"]}},
      {"gameId":"how-to-fish","title":"How to Fish","exeRelativePath":"How to Fish.exe","order":1,
       "mods":{"enabled":true,"community":"how-to-fish","loader":"bepinex",
               "steamAppId":"4001890","steamFolder":"How to Fish/How to Fish","exeNames":["How to Fish.exe"]}},
      {"gameId":"drive-beyond-horizons","title":"Drive","exeRelativePath":"Drive.exe","order":2}
    ]}`)

	modsDir := filepath.Join(root, "manifests", "_mods", "lethal-company")
	writeFile(t, filepath.Join(modsDir, "latest.json"), `{"version":"ASTeam-LethalReloaded-2.2.12"}`)
	writeFile(t, filepath.Join(modsDir, "ASTeam-LethalReloaded-2.2.12.json"), `{"version":"ASTeam-LethalReloaded-2.2.12","files":[]}`)
	writeFile(t, filepath.Join(modsDir, "sources", "ASTeam-LethalReloaded-2.2.12.json"),
		`{"kind":"thunderstore","displayName":"Lethal Reloaded","tree":["a-b-1.0.0"]}`)

	return root
}

func gamesFromAPI(t *testing.T) map[string]GameInfo {
	t.Helper()
	rec := httptest.NewRecorder()
	testRouter().ServeHTTP(rec, httptest.NewRequest(http.MethodGet, "/api/games", nil))
	if rec.Code != http.StatusOK {
		t.Fatalf("GET /api/games = %d", rec.Code)
	}
	var resp GamesResponse
	if err := json.Unmarshal(rec.Body.Bytes(), &resp); err != nil {
		t.Fatalf("разбор ответа: %v", err)
	}
	out := make(map[string]GameInfo, len(resp.Items))
	for _, g := range resp.Items {
		out[g.GameID] = g
	}
	return out
}

func TestGamesExposeActiveModpack(t *testing.T) {
	modsFixture(t)
	byID := gamesFromAPI(t)

	lc, ok := byID["lethal-company"]
	if !ok || lc.Mods == nil {
		t.Fatalf("у lethal-company нет блока mods: %+v", lc)
	}
	if !lc.Mods.HasLatest {
		t.Error("активный модпак не отдан как hasLatest")
	}
	if lc.Mods.Version != "ASTeam-LethalReloaded-2.2.12" {
		t.Errorf("версия %q", lc.Mods.Version)
	}
	// The launcher shows this verbatim on the game card; the version name on
	// its own reads as noise to a player.
	if lc.Mods.DisplayName != "Lethal Reloaded" || lc.Mods.DisplayVersion != "2.2.12" {
		t.Errorf("отображаемое имя %q / %q", lc.Mods.DisplayName, lc.Mods.DisplayVersion)
	}
	if lc.Mods.ManifestURL != "/manifests/_mods/lethal-company/ASTeam-LethalReloaded-2.2.12.json" {
		t.Errorf("manifestUrl %q", lc.Mods.ManifestURL)
	}
	if lc.Mods.ContentBaseURL != "/content/_mods/lethal-company/ASTeam-LethalReloaded-2.2.12/files" {
		t.Errorf("contentBaseUrl %q", lc.Mods.ContentBaseURL)
	}
	if lc.Mods.SteamAppID != "1966720" || lc.Mods.Loader != "bepinex" {
		t.Errorf("метаданные Steam/загрузчика: %+v", lc.Mods)
	}
}

func TestGamesWithoutBuiltModpackStillCarryMetadata(t *testing.T) {
	modsFixture(t)
	byID := gamesFromAPI(t)

	// Mods configured, nothing built yet. The launcher still needs the Steam
	// fields: without a pack it can offer "запустить свою копию без модов", and
	// dropping the block entirely would hide that option.
	htf := byID["how-to-fish"]
	if htf.Mods == nil {
		t.Fatal("блок mods пропал у игры без собранного модпака")
	}
	if htf.Mods.HasLatest {
		t.Error("hasLatest=true, хотя ничего не собрано")
	}
	if htf.Mods.SteamFolder != "How to Fish/How to Fish" {
		// The nested folder is the whole reason this field exists.
		t.Errorf("steamFolder %q", htf.Mods.SteamFolder)
	}
	if htf.Mods.ManifestURL != "" || htf.Mods.Version != "" {
		t.Errorf("ссылки на несуществующую версию: %+v", htf.Mods)
	}
}

func TestGamesWithoutModsHaveNoModsBlock(t *testing.T) {
	modsFixture(t)
	byID := gamesFromAPI(t)
	if g := byID["drive-beyond-horizons"]; g.Mods != nil {
		t.Errorf("у игры без модов появился блок mods: %+v", g.Mods)
	}
}

func TestModsNamespaceIsNotAGame(t *testing.T) {
	// The scan fallback runs when there is no registry. _mods and _registry are
	// directories under manifests/ that are not games; listing them would show
	// players a game called "_mods" whose Play button does nothing.
	root := withContentRoot(t)
	for _, d := range []string{"lethal-company", "_mods", "_registry"} {
		if err := os.MkdirAll(filepath.Join(root, "manifests", d), 0o755); err != nil {
			t.Fatal(err)
		}
	}
	byID := gamesFromAPI(t)
	if _, bad := byID["_mods"]; bad {
		t.Error("_mods отдан как игра")
	}
	if _, bad := byID["_registry"]; bad {
		t.Error("_registry отдан как игра")
	}
	if _, ok := byID["lethal-company"]; !ok {
		t.Error("настоящая игра пропала из выдачи")
	}
}

func TestModsInfoForIgnoresDisabledConfig(t *testing.T) {
	withContentRoot(t)
	if info := modsInfoFor(games.Entry{GameID: "x"}); info != nil {
		t.Error("у игры без конфигурации модов не должно быть блока")
	}
	off := games.Entry{GameID: "x", Mods: &games.ModsConfig{Enabled: false, Community: "c"}}
	if info := modsInfoFor(off); info != nil {
		t.Error("выключенные моды не должны отдаваться лаунчеру")
	}
}

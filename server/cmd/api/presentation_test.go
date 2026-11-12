package main

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

func TestPublicGamesExposePresentationWithoutLocalBuild(t *testing.T) {
	root := withContentRoot(t)
	dir := filepath.Join(root, "manifests", "_registry")
	if err := os.MkdirAll(dir, 0o750); err != nil {
		t.Fatal(err)
	}
	body := `{"items":[{"gameId":"peak","title":"PEAK","mods":{"enabled":true,"steamAppId":"3527290","exeNames":["PEAK.exe"]},"presentation":{"description":"Играйте вместе","modpackDescription":"Для большой компании","instructions":["Откройте Steam"],"media":[{"kind":"video","url":"https://launcher.samoy.love/assets/trailer.mp4"}],"mods":[{"id":"more","name":"More Players","russianName":"Больше игроков"}]}},{"gameId":"unsafe","title":"Unsafe","presentation":{"heroUrl":"file:///C:/secret"}},{"gameId":"hidden","unpublished":true,"presentation":{"description":"Secret"}}]}`
	if err := os.WriteFile(filepath.Join(dir, "games.json"), []byte(body), 0o600); err != nil {
		t.Fatal(err)
	}
	w := httptest.NewRecorder()
	testRouter().ServeHTTP(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil))
	if w.Code != http.StatusOK {
		t.Fatal(w.Code, w.Body.String())
	}
	var response GamesResponse
	if err := json.Unmarshal(w.Body.Bytes(), &response); err != nil {
		t.Fatal(err)
	}
	if len(response.Items) != 2 {
		t.Fatalf("unexpected public entries: %+v", response.Items)
	}
	game := response.Items[0]
	if game.HasLatest || game.Mods == nil || game.Presentation == nil || game.Presentation.Description != "Играйте вместе" || game.Presentation.ModpackDescription != "Для большой компании" || len(game.Presentation.Media) != 1 || len(game.Presentation.Mods) != 1 {
		t.Fatalf("mods-only presentation was not exposed: %+v", game)
	}
	if response.Items[1].Presentation != nil {
		t.Fatal("unsafe manually edited media exposed publicly")
	}
}

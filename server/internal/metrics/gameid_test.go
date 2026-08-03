package metrics

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"testing"
)

// writeRegistry lays out manifests/_registry/games.json with the given ids.
func writeRegistry(t *testing.T, root string, ids ...string) {
	t.Helper()
	dir := filepath.Join(root, "manifests", "_registry")
	if err := os.MkdirAll(dir, 0o750); err != nil {
		t.Fatal(err)
	}
	type item struct {
		GameID string `json:"gameId"`
		Title  string `json:"title"`
	}
	reg := struct {
		Items []item `json:"items"`
	}{Items: make([]item, 0, len(ids))}
	for _, id := range ids {
		reg.Items = append(reg.Items, item{GameID: id, Title: id})
	}
	body, err := json.Marshal(reg)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "games.json"), body, 0o600); err != nil {
		t.Fatal(err)
	}
}

// The bug this gate exists for: the per-game table of the admin panel filled up
// with rows named after a 32-char hex id, all counters zero. They pass every
// character check — the only thing wrong with them is that no such game exists.
func TestSubmitRejectsGameIDOutsideRegistry(t *testing.T) {
	root := t.TempDir()
	writeRegistry(t, root, "drive-beyond-horizons")
	h := New(root)

	w := submit(t, h, `{"event":"game_launch","gameId":"00d378defbff4348ab226f84361fec64"}`)
	if w.Code != http.StatusBadRequest {
		t.Fatalf("hex gameId accepted: %d %s", w.Code, w.Body.String())
	}
	if w := submit(t, h, `{"event":"game_launch","gameId":"drive-beyond-horizons"}`); w.Code != http.StatusOK {
		t.Fatalf("real gameId rejected: %d %s", w.Code, w.Body.String())
	}

	s := summary(t, h, "")
	if len(s.ByGame) != 1 || s.ByGame[0].GameID != "drive-beyond-horizons" {
		t.Fatalf("byGame = %+v", s.ByGame)
	}
	if s.Totals.Events != 1 {
		t.Errorf("events = %d, want 1", s.Totals.Events)
	}
}

// launcher_start names no game and must stay acceptable.
func TestSubmitAllowsEmptyGameID(t *testing.T) {
	root := t.TempDir()
	writeRegistry(t, root, "g1")
	h := New(root)

	if w := submit(t, h, `{"event":"launcher_start","appVersion":"1.2.3.0"}`); w.Code != http.StatusOK {
		t.Fatalf("launcher_start rejected: %d %s", w.Code, w.Body.String())
	}
	if s := summary(t, h, ""); s.Totals.LauncherStarts != 1 {
		t.Errorf("launcherStarts = %d, want 1", s.Totals.LauncherStarts)
	}
}

// Without a readable registry the format check is all there is: dropping every
// event because one file is missing would be worse than the rows it prevents.
func TestSubmitFallsBackToFormatWithoutRegistry(t *testing.T) {
	h := New(t.TempDir())

	if w := submit(t, h, `{"event":"game_launch","gameId":"g1"}`); w.Code != http.StatusOK {
		t.Fatalf("plausible gameId rejected without a registry: %d %s", w.Code, w.Body.String())
	}
	for _, bad := range []string{"../etc", "_registry", "a b"} {
		if w := submit(t, h, `{"event":"game_launch","gameId":"`+bad+`"}`); w.Code != http.StatusBadRequest {
			t.Errorf("gameId %q accepted: %d", bad, w.Code)
		}
	}
}

// The events stored before the gate existed are still in the file. The summary
// applies the same check on read, so they leave the panel without the file
// being rewritten or cleared.
func TestSummaryHidesStoredEventsOutsideRegistry(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	// Accepted while there was no registry to check against.
	for _, body := range []string{
		`{"event":"game_launch","gameId":"00d378defbff4348ab226f84361fec64"}`,
		`{"event":"game_install","gameId":"drive-beyond-horizons","result":"ok","bytes":100}`,
	} {
		if w := submit(t, h, body); w.Code != http.StatusOK {
			t.Fatalf("setup submit failed: %d %s", w.Code, w.Body.String())
		}
	}

	writeRegistry(t, root, "drive-beyond-horizons")
	s := summary(t, h, "")
	if len(s.ByGame) != 1 || s.ByGame[0].GameID != "drive-beyond-horizons" {
		t.Fatalf("byGame = %+v", s.ByGame)
	}
	// The hidden line is dropped whole, so the headline numbers still agree with
	// the breakdown below them.
	if s.Totals.Events != 1 || s.Totals.GameLaunches != 0 {
		t.Errorf("totals = %d events, %d launches; want 1 and 0", s.Totals.Events, s.Totals.GameLaunches)
	}
}

// A game added in the admin panel must start counting without a restart.
func TestRegistryCacheFollowsFileChanges(t *testing.T) {
	root := t.TempDir()
	writeRegistry(t, root, "g1")
	h := New(root)

	if w := submit(t, h, `{"event":"game_launch","gameId":"g2"}`); w.Code != http.StatusBadRequest {
		t.Fatalf("g2 accepted before it was registered: %d", w.Code)
	}
	writeRegistry(t, root, "g1", "g2")
	if w := submit(t, h, `{"event":"game_launch","gameId":"g2"}`); w.Code != http.StatusOK {
		t.Fatalf("g2 rejected after being registered: %d %s", w.Code, w.Body.String())
	}
}

package games

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"reflect"
	"strings"
	"testing"
)

func samplePresentation() *Presentation {
	return &Presentation{Description: "Исследуйте остров вместе", Genre: "Кооператив",
		ModpackDescription: "Позволяет играть большой компанией",
		ModpackVersion:     "Lart_Iste-PeakFriendsEdition-2.0.15", ModpackRevision: "0123456789abcdef0123456789abcdef",
		HeroURL: "/assets/games/peak/hero.jpg", Highlights: []string{"До четырёх игроков"},
		Media: []GameMedia{{Kind: "video", URL: "https://video.akamai.steamstatic.com/trailer.mp4", PosterURL: "/assets/peak/poster.jpg", Caption: "Трейлер"}},
		Mods:  []ModDescription{{ID: "more-players", Name: "More Players", RussianName: "Больше игроков", Description: "Снимает лимит группы", Category: "Совместная игра", Version: "1.2", URL: "https://thunderstore.io/c/peak/"}},
	}
}

func TestPresentationExternalMediaAndBenefits(t *testing.T) {
	p := samplePresentation()
	p.ModBenefits = []ModBenefit{{Title: "Большая команда", Description: "Отправляйтесь за добычей вместе с друзьями", Icon: "users"}}
	p.Media = append(p.Media, GameMedia{Kind: "video", URL: "https://www.youtube.com/watch?v=GWuF23poTf4", Provider: "youtube", Role: "gameplay", SourceURL: "https://www.youtube.com/watch?v=GWuF23poTf4", Author: "Marmok", PublishedAt: "2025-03-07"})
	if err := ValidatePresentation(p); err != nil {
		t.Fatal(err)
	}
	for _, bad := range []string{"https://evil.example/watch?v=GWuF23poTf4", "https://youtube.com/redirect?q=https://evil.example", "https://youtu.be/not-valid", "https://rutube.ru/video/abcdef"} {
		p.Media[1].URL = bad
		if ValidatePresentation(p) == nil {
			t.Fatalf("accepted external URL: %s", bad)
		}
	}
	p.Media = p.Media[:1]
	p.ModBenefits[0].Icon = "script"
	if ValidatePresentation(p) == nil {
		t.Fatal("accepted unknown icon")
	}
	p.ModBenefits[0].Icon = "package"
	p.Media[0].Width = 1920
	if ValidatePresentation(p) == nil {
		t.Fatal("accepted missing height")
	}
	p.Media[0].Height = 1080
	p.Media[0].PublishedAt = "2026-02-30"
	if ValidatePresentation(p) == nil {
		t.Fatal("accepted invalid date")
	}
}

func TestRegistryIfMatchRejectsStaleEditorialSnapshot(t *testing.T) {
	h := New(t.TempDir())
	saveRegistry(t, h, []Entry{{GameID: "peak", Title: "Before"}})
	first := httptest.NewRecorder()
	h.Get(first, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/games", nil))
	etag := first.Header().Get("ETag")
	if etag == "" {
		t.Fatal("missing registry revision")
	}
	saveRaw(t, h, `{"items":[{"gameId":"peak","title":"Concurrent edit"}]}`)
	r := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/games/save", strings.NewReader(`{"items":[{"gameId":"peak","title":"Stale snapshot"}]}`))
	r.Header.Set("If-Match", etag)
	w := httptest.NewRecorder()
	h.Save(w, r)
	if w.Code != http.StatusPreconditionFailed {
		t.Fatalf("stale save accepted: %d", w.Code)
	}
	got, _ := h.Entry("peak")
	if got.Title != "Concurrent edit" {
		t.Fatal("concurrent edit was overwritten")
	}
	current := httptest.NewRecorder()
	h.Get(current, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/games", nil))
	r = httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/games/save", strings.NewReader(`{"items":[{"gameId":"peak","title":"Fresh snapshot"}]}`))
	r.Header.Set("If-Match", current.Header().Get("ETag"))
	w = httptest.NewRecorder()
	h.Save(w, r)
	if w.Code != http.StatusOK || w.Header().Get("ETag") == etag {
		t.Fatalf("fresh save failed: %d", w.Code)
	}
}

func TestPresentationRoundTripPreservesOmittedFieldAndAllowsClear(t *testing.T) {
	h := New(t.TempDir())
	want := samplePresentation()
	saveRegistry(t, h, []Entry{{GameID: "peak", Title: "PEAK", Presentation: want}})
	saveRaw(t, h, `{"items":[{"gameId":"peak","title":"New title"}]}`)
	got, _ := h.Entry("peak")
	if !reflect.DeepEqual(got.Presentation, want) {
		t.Fatalf("presentation lost: %+v", got.Presentation)
	}
	saveRaw(t, h, `{"items":[{"gameId":"peak","presentation":{"description":"Новое описание"}}]}`)
	got, _ = h.Entry("peak")
	if got.Presentation.Description != "Новое описание" || got.Presentation.ModpackDescription != want.ModpackDescription || len(got.Presentation.Media) != 1 {
		t.Fatal("partial update damaged the stored presentation")
	}
	saveRaw(t, h, `{"items":[{"gameId":"peak","presentation":null}]}`)
	got, _ = h.Entry("peak")
	if got.Presentation != nil {
		t.Fatal("explicit null did not clear presentation")
	}
}

func TestInvalidPresentationNeverChangesRegistry(t *testing.T) {
	h := New(t.TempDir())
	saveRegistry(t, h, []Entry{{GameID: "peak", Title: "PEAK", Presentation: samplePresentation()}})
	before, err := os.ReadFile(h.registryPath())
	if err != nil {
		t.Fatal(err)
	}
	for _, bad := range []string{
		`{"media":[{"kind":"video","url":"javascript:alert(1)"}]}`,
		`{"media":[{"kind":"iframe","url":"https://example.com/video"}]}`,
		`{"mods":[{"id":"same","name":"one"},{"id":"SAME","name":"two"}]}`,
		`{"highlights":[""]}`,
		`{"description":` + string(mustJSON(t, strings.Repeat("я", 12001))) + `}`,
		`{"modpackDescription":` + string(mustJSON(t, strings.Repeat("я", 2001))) + `}`,
		`{"modpackVersion":` + string(mustJSON(t, strings.Repeat("x", 201))) + `}`,
		`{"modpackRevision":"not-a-revision"}`,
	} {
		w := httptest.NewRecorder()
		h.Save(w, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/games/save", strings.NewReader(`{"items":[{"gameId":"peak","presentation":`+bad+`}]}`)))
		if w.Code != http.StatusBadRequest {
			t.Fatalf("invalid presentation accepted: %d %s", w.Code, w.Body.String())
		}
		after, err := os.ReadFile(h.registryPath())
		if err != nil {
			t.Fatal(err)
		}
		if string(before) != string(after) {
			t.Fatal("rejected edit changed the registry")
		}
	}
}

func mustJSON(t *testing.T, value any) []byte {
	t.Helper()
	b, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestPresentationURLPolicy(t *testing.T) {
	for _, good := range []string{"/assets/a.jpg", "/gallery/peak/a.jpg", "/manifests/peak/icon.png", "https://cdn.example.com/a.mp4?t=2"} {
		if err := presentationURL("url", good, false); err != nil {
			t.Errorf("%q: %v", good, err)
		}
	}
	for _, bad := range []string{"", "javascript:x", "file:///C:/a", "http://example.com/a", "//example.com/a", "https://u:p@example.com/a", "/assets/../secret", "/assets/%2e%2e/secret", "/admin/api/health", "https://localhost/a", "https://127.0.0.1/a", "https://10.0.0.1/a", "https://example.com/a#x", "https://example.com/a\n"} {
		if err := presentationURL("url", bad, false); err == nil {
			t.Errorf("unsafe URL accepted: %q", bad)
		}
	}
}

func TestModsOnlyPresentationDoesNotRequireLocalBuild(t *testing.T) {
	h := New(t.TempDir())
	saveRegistry(t, h, []Entry{{GameID: "peak", Title: "PEAK", Mods: &ModsConfig{Enabled: true, SteamAppID: "3527290", ExeNames: []string{"PEAK.exe"}}, Presentation: samplePresentation()}})
	got, _ := h.Entry("peak")
	if got.ExeRelativePath != "" || got.Presentation == nil || got.Mods == nil {
		t.Fatal("mods-only entry did not survive")
	}
}

func TestEmptyPresentationArraysNeverSerializeAsNull(t *testing.T) {
	raw := string(mustJSON(t, &Presentation{Description: "Text"}))
	if strings.Contains(raw, ":null") {
		t.Fatalf("native Vec fields cannot read null: %s", raw)
	}
}

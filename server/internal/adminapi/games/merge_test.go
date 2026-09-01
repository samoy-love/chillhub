package games

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// saveRaw отправляет тело как есть — панель шлёт именно такое: строку игры из
// шести полей, без всего остального, что лежит в записи реестра.
func saveRaw(t *testing.T, h *Handlers, body string) *httptest.ResponseRecorder {
	t.Helper()
	r := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/games/save", strings.NewReader(body))
	w := httptest.NewRecorder()
	h.Save(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("save returned %d: %s", w.Code, w.Body.String())
	}
	return w
}

// modded кладёт в реестр игру с настроенными модами — так её оставляют
// обработчики вкладки «Моды».
func modded(t *testing.T, h *Handlers) {
	t.Helper()
	saveRegistry(t, h, []Entry{{GameID: "lethal-company", Title: "Lethal Company"}})
	e, ok := h.Entry("lethal-company")
	if !ok {
		t.Fatal("игра не попала в реестр")
	}
	if err := h.SaveMods(e.GameID, &ModsConfig{
		Enabled:     true,
		Community:   "lethal-company",
		SteamAppID:  "1966720",
		SteamFolder: "Lethal Company",
		Loader:      "bepinex",
	}); err != nil {
		t.Fatal(err)
	}
}

// СОХРАНЕНИЕ СПИСКА ИГР НЕ ДОЛЖНО СТИРАТЬ НАСТРОЙКИ МОДОВ.
//
// Панель шлёт шесть полей строки, а запись шире: настройку модов пишут совсем
// другие обработчики. Полная замена реестра присланным стирала её у всех игр
// при каждом «Сохранить» — в том числе при перетаскивании строки мышью, — и со
// стороны оператора это выглядело как «Моды для этой игры не настроены»
// назавтра после того, как он их настроил.
func TestSaveKeepsModsConfiguration(t *testing.T) {
	h := New(t.TempDir())
	modded(t, h)

	saveRaw(t, h, `{"items":[{"gameId":"lethal-company","title":"Lethal Company","iconUrl":"","exeRelativePath":"LC.exe","order":0,"pinned":false}]}`)

	e, ok := h.Entry("lethal-company")
	if !ok {
		t.Fatal("игра исчезла из реестра")
	}
	if e.Mods == nil {
		t.Fatal("настройка модов стёрта сохранением списка игр")
	}
	if e.Mods.Community != "lethal-company" || e.Mods.SteamAppID != "1966720" {
		t.Errorf("настройка модов изменилась: %+v", e.Mods)
	}
	// А присланные поля обязаны примениться — иначе «сохранить» перестало бы
	// сохранять.
	if e.ExeRelativePath != "LC.exe" {
		t.Errorf("путь к exe не сохранён: %q", e.ExeRelativePath)
	}
}

// Присланное поле перекрывает сохранённое: слияние сберегает молчание, а не
// отменяет правки.
func TestSaveAppliesSentFieldsOverStored(t *testing.T) {
	h := New(t.TempDir())
	modded(t, h)

	saveRaw(t, h, `{"items":[{"gameId":"lethal-company","title":"Летал Компани","pinned":true}]}`)

	e, _ := h.Entry("lethal-company")
	if e.Title != "Летал Компани" {
		t.Errorf("название не применилось: %q", e.Title)
	}
	if !e.Pinned {
		t.Error("закрепление не применилось")
	}
	if e.Mods == nil {
		t.Error("настройка модов всё-таки стёрта")
	}
}

// Удаление игры из списка по-прежнему удаляет её вместе с настройками: слияние
// касается полей, а не строк.
func TestSaveStillRemovesGames(t *testing.T) {
	h := New(t.TempDir())
	modded(t, h)
	saveRegistry(t, h, []Entry{{GameID: "how-to-fish", Title: "How to Fish"}})

	if _, ok := h.Entry("lethal-company"); ok {
		t.Error("удалённая игра осталась в реестре")
	}
}

// Новая игра приходит без сохранённой пары — сливать не с чем, и это не повод
// отказать.
func TestSaveAcceptsBrandNewGame(t *testing.T) {
	h := New(t.TempDir())

	saveRaw(t, h, `{"items":[{"gameId":"peak","title":"PEAK","order":0}]}`)

	e, ok := h.Entry("peak")
	if !ok {
		t.Fatal("новая игра не сохранилась")
	}
	if e.Title != "PEAK" || e.Mods != nil {
		t.Errorf("новая игра сохранена как %+v", e)
	}
}

// Порядок строк задаёт панель их последовательностью, и слияние не должно
// возвращать старый order из реестра — иначе перетаскивание мышью молча
// откатывалось бы.
func TestSaveKeepsNewOrderNotStoredOne(t *testing.T) {
	h := New(t.TempDir())
	saveRegistry(t, h, []Entry{{GameID: "a", Title: "A"}, {GameID: "b", Title: "B"}})

	saveRaw(t, h, `{"items":[{"gameId":"b","title":"B"},{"gameId":"a","title":"A"}]}`)

	items := decodeItems(t, h.Get, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/games", nil))
	if len(items) != 2 || items[0].GameID != "b" {
		t.Errorf("порядок после перестановки: %+v", items)
	}
}

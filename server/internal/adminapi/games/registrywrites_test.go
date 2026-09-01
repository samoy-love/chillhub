package games

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// ПОДТЯГИВАНИЕ ИЗ THUNDERSTORE НЕ ДОЛЖНО ОТМАТЫВАТЬ РЕЕСТР НАЗАД.
//
// Обработчик вкладки «Моды» читает строку игры, уходит в сеть на две минуты и
// кладёт прочитанное обратно. Пока он висит, оператор в другой вкладке
// переименовывает игру и снимает её с публикации — и всё это молча
// возвращается к состоянию двухминутной давности, потому что запись
// заменялась целиком. Для unpublished это значит, что снятая с публикации игра
// снова видна игрокам, а ответ — «ok».
func TestSaveModsKeepsEditsMadeWhileThunderstoreWasAnswering(t *testing.T) {
	h := New(t.TempDir())
	saveRegistry(t, h, []Entry{{GameID: "lethal-company", Title: "Старое имя"}})

	// Обработчик прочитал запись и ушёл в сеть.
	stale, ok := h.Entry("lethal-company")
	if !ok {
		t.Fatal("игра не попала в реестр")
	}

	// Оператор тем временем правит реестр.
	saveRaw(t, h, `{"items":[{"gameId":"lethal-company","title":"Новое имя","iconUrl":"","exeRelativePath":"LC.exe","order":0,"pinned":true,"unpublished":true}]}`)

	// Сеть ответила: сохраняется только настройка модов.
	cfg := stale.Mods
	if cfg == nil {
		cfg = &ModsConfig{}
	}
	cfg.Enabled = true
	cfg.Community = "lethal-company"
	if err := h.SaveMods("lethal-company", cfg); err != nil {
		t.Fatal(err)
	}

	after, ok := h.Entry("lethal-company")
	if !ok {
		t.Fatal("игра исчезла из реестра")
	}
	if !after.Unpublished {
		t.Error("снятая с публикации игра снова видна игрокам")
	}
	if after.Title != "Новое имя" {
		t.Errorf("название откатилось: %q", after.Title)
	}
	if !after.Pinned {
		t.Error("закрепление откатилось")
	}
	if after.ExeRelativePath != "LC.exe" {
		t.Errorf("путь к exe откатился: %q", after.ExeRelativePath)
	}
	if after.Mods == nil || !after.Mods.Enabled || after.Mods.Community != "lethal-company" {
		t.Errorf("настройка модов не сохранена: %+v", after.Mods)
	}
}

// Реестр целиком буферизовался в памяти без всякого потолка: у админского
// сервера таймауты чтения намеренно нулевые, а nginx пропускает на эти
// маршруты 30 ГБ ради заливки сборок. Тот же процесс обслуживает публичные
// /feedback/submit и /metrics/report, так что его падение — это потеря приёма
// телеметрии и обратной связи, а не только недоступность панели.
func TestSaveRefusesARegistryLargerThanAnyRealOne(t *testing.T) {
	h := New(t.TempDir())
	saveRegistry(t, h, []Entry{{GameID: "raft", Title: "Рафт"}})

	huge := `{"items":[{"gameId":"raft","title":"` + strings.Repeat("я", maxRegistryBytes) + `"}]}`
	w := httptest.NewRecorder()
	h.Save(w, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/games/save", strings.NewReader(huge)))

	if w.Code == http.StatusOK {
		t.Fatalf("реестр без потолка принят: %d", w.Code)
	}
	after, ok := h.Entry("raft")
	if !ok {
		t.Fatal("игра исчезла из реестра")
	}
	if after.Title != "Рафт" {
		t.Errorf("отвергнутое сохранение всё-таки записалось: %q", after.Title[:min(len(after.Title), 40)])
	}
}

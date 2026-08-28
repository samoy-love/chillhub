package feedback

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// getLogs calls the download endpoint for one report.
func getLogs(t *testing.T, h *Handlers, id string) *httptest.ResponseRecorder {
	t.Helper()
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/feedback/logs?id="+id, nil)
	w := httptest.NewRecorder()
	h.Logs(w, r)
	return w
}

// submitWithLogs posts a report carrying a diagnostics bundle and returns its id.
func submitWithLogs(t *testing.T, h *Handlers, logs string) string {
	t.Helper()
	body := `{"type":"bug","comment":"не запускается","attachLogs":true,"logs":"` + logs + `"}`
	if w := postReport(t, h, body); w.Code != http.StatusOK {
		t.Fatalf("submit returned %d: %s", w.Code, w.Body.String())
	}
	items := listItems(t, h)
	if len(items) != 1 {
		t.Fatalf("inbox holds %d items, want 1", len(items))
	}
	return items[0].ID
}

// Скачивание журнала — то, ради чего обращение открывают: «у меня не
// запускается» без журнала не разбирается вовсе.
func TestLogsAreDownloadableAsAFile(t *testing.T) {
	h := New(t.TempDir())
	id := submitWithLogs(t, h, "строка первая\\nстрока последняя")

	w := getLogs(t, h, id)

	if w.Code != http.StatusOK {
		t.Fatalf("logs returned %d: %s", w.Code, w.Body.String())
	}
	if got := w.Body.String(); !strings.Contains(got, "строка последняя") {
		t.Errorf("тело не содержит журнала: %q", got)
	}
	if ct := w.Header().Get("Content-Type"); !strings.HasPrefix(ct, "text/plain") {
		t.Errorf("Content-Type %q — журнал обязан приезжать текстом", ct)
	}
	// Без Content-Disposition браузер показывает мегабайт лога во вкладке, и
	// «скачать» превращается в «выделить всё и скопировать».
	cd := w.Header().Get("Content-Disposition")
	if !strings.HasPrefix(cd, "attachment;") || !strings.Contains(cd, id) {
		t.Errorf("Content-Disposition %q не предлагает сохранить файл с именем обращения", cd)
	}
}

// Обращение есть, журнала нет — это не «ошиблись ссылкой», и отвечать 404
// на такое значит путать две разные ситуации.
func TestReportWithoutLogsAnswersNoContent(t *testing.T) {
	h := New(t.TempDir())
	if w := postReport(t, h, `{"type":"idea","comment":"добавьте тёмную тему"}`); w.Code != http.StatusOK {
		t.Fatalf("submit returned %d", w.Code)
	}
	id := listItems(t, h)[0].ID

	if got := getLogs(t, h, id).Code; got != http.StatusNoContent {
		t.Errorf("обращение без журнала ответило %d, ожидался %d", got, http.StatusNoContent)
	}
}

func TestUnknownReportLogsAreNotFound(t *testing.T) {
	h := New(t.TempDir())

	if got := getLogs(t, h, "нет-такого").Code; got != http.StatusNotFound {
		t.Errorf("несуществующее обращение ответило %d", got)
	}
	if got := getLogs(t, h, "").Code; got != http.StatusBadRequest {
		t.Errorf("запрос без id ответил %d", got)
	}
}

// СПИСОК НЕ ТАЩИТ ЖУРНАЛЫ.
//
// В ящике до двух тысяч обращений, у каждого до мегабайта диагностики, и панель
// перезапрашивает список на каждое действие — прочитать, пометить важным,
// удалить. Отдавать при этом гигабайты ради имени и первой строки комментария
// значит платить временем оператора за данные, которые он не просил.
func TestListOmitsLogsButKeepsTheirSize(t *testing.T) {
	h := New(t.TempDir())
	id := submitWithLogs(t, h, strings.Repeat("x", 4096))

	items := listItems(t, h)

	if items[0].Logs != "" {
		t.Errorf("список принёс журнал целиком: %d байт", len(items[0].Logs))
	}
	if items[0].LogBytes != 4096 {
		t.Errorf("размер журнала в списке %d, ожидалось 4096", items[0].LogBytes)
	}
	if !items[0].AttachLogs {
		t.Error("признак «журнал приложен» пропал вместе с текстом")
	}

	// А по своему адресу журнал приезжает целиком — иначе прятать его в списке
	// было бы просто потерей.
	if got := len(getLogs(t, h, id).Body.String()); got != 4096 {
		t.Errorf("скачано %d байт из 4096", got)
	}
}

// Карточка обращения журнал по-прежнему отдаёт: панель показывает из него хвост,
// не дожидаясь скачивания файла.
func TestGetStillCarriesLogs(t *testing.T) {
	h := New(t.TempDir())
	id := submitWithLogs(t, h, "внутри")

	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/feedback/get?id="+id, nil)
	w := httptest.NewRecorder()
	h.Get(w, r)

	if w.Code != http.StatusOK {
		t.Fatalf("get returned %d", w.Code)
	}
	if !strings.Contains(w.Body.String(), "внутри") {
		t.Error("карточка обращения осталась без журнала")
	}
}

func TestSafeFileIDKeepsOnlyFileNameCharacters(t *testing.T) {
	if got := safeFileID("ab12-CD_ef"); got != "ab12-CD_ef" {
		t.Errorf("обычный идентификатор изменён: %q", got)
	}
	if got := safeFileID(`a"/..\b`); got != "a_____b" {
		t.Errorf("опасные символы не заменены: %q", got)
	}
	if got := safeFileID(""); got != "report" {
		t.Errorf("пустой идентификатор дал имя %q", got)
	}
}

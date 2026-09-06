package mods

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
)

// Чем панель заполняет тело запроса на сборку.
//
// Сборка и пересборка — единственные записи админки, которые идут мимо общего
// слоя запросов: они читают поток NDJSON и потому зовут fetch напрямую. Ровно
// поэтому у них своё тело, и ровно поэтому оно однажды разошлось с сервером:
// панель слала разбор с "content-type: application/json", а сервер читает
// r.FormValue. Разбор он не видит вовсе — gameId приходит пустым, и обе кнопки
// отвечают 400, не начав работу.
//
// Тесты панели этого не ловили: они смотрели на объект ДО отправки, а не на то,
// что уходит по проводу. Здесь проверяется вторая половина того же стыка — что
// сервер понимает именно ту кодировку, которой панель шлёт.

// panelPost повторяет запрос панели: то же тело, тот же заголовок.
func panelPost(t *testing.T, fn http.HandlerFunc, ctype, body string) *httptest.ResponseRecorder {
	t.Helper()
	req := httptest.NewRequest(http.MethodPost, "/admin/api/mods/x", strings.NewReader(body))
	req.Header.Set("Content-Type", ctype)
	rec := httptest.NewRecorder()
	fn(rec, req)
	return rec
}

func TestBuildReadsTheFormBodyThePanelSends(t *testing.T) {
	fs := newFakeStore(t)
	seedTwoLoaderPack(fs)
	h, _ := testHandlers(t, fs)

	form := url.Values{
		"gameId":    {"lethal-company"},
		"namespace": {"Team"},
		"name":      {"Pack"},
		"version":   {"1.0.0"},
	}.Encode()

	rec := panelPost(t, h.Build, "application/x-www-form-urlencoded", form)
	if rec.Code != http.StatusOK || strings.Contains(rec.Body.String(), `type":"error"`) {
		t.Fatalf("сборка тем телом, каким её шлёт панель, не прошла: %d %s", rec.Code, rec.Body.String())
	}
}

func TestBuildRefusesAJSONBodyInsteadOfBuildingTheWrongThing(t *testing.T) {
	fs := newFakeStore(t)
	seedTwoLoaderPack(fs)
	h, _ := testHandlers(t, fs)

	body := `{"gameId":"lethal-company","namespace":"Team","name":"Pack","version":"1.0.0"}`
	rec := panelPost(t, h.Build, "application/json", body)
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("разбор вместо формы обязан быть отказом, а не догадкой: %d %s", rec.Code, rec.Body.String())
	}
}

func TestRebuildReadsTheFormBodyThePanelSends(t *testing.T) {
	fs := newFakeStore(t)
	seedTwoLoaderPack(fs)
	h, _ := testHandlers(t, fs)

	form := url.Values{"gameId": {"lethal-company"}, "namespace": {"Team"}, "name": {"Pack"}, "version": {"1.0.0"}}.Encode()
	if rec := panelPost(t, h.Build, "application/x-www-form-urlencoded", form); rec.Code != http.StatusOK {
		t.Fatalf("подготовка: сборка не прошла: %d %s", rec.Code, rec.Body.String())
	}

	// Пересборка называет только игру и версию: состав она читает из записи
	// рядом с манифестом, а не из тела запроса.
	again := url.Values{"gameId": {"lethal-company"}, "version": {"Team-Pack-1.0.0"}}.Encode()
	rec := panelPost(t, h.Rebuild, "application/x-www-form-urlencoded", again)
	if rec.Code != http.StatusOK || strings.Contains(rec.Body.String(), `type":"error"`) {
		t.Fatalf("пересборка тем телом, каким её шлёт панель, не прошла: %d %s", rec.Code, rec.Body.String())
	}
}

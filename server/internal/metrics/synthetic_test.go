package metrics

import (
	"net/http"
	"strings"
	"testing"
)

// Свои прогоны считались игроками: установка автотестом добавляла и событие, и
// «уникального пользователя», причём каждый раз нового.
func TestSyntheticEventsStayOutOfSummary(t *testing.T) {
	h := New(t.TempDir())
	for _, b := range []string{
		`{"installId":"aaa","event":"launcher_start","appVersion":"1.4.0"}`,
		`{"installId":"aaa","event":"game_session","gameId":"g1","result":"ok","durationMs":60000}`,
		`{"installId":"test-ci-01","event":"launcher_start","appVersion":"0.0.0-ci"}`,
		`{"installId":"test-ci-01","event":"game_install","gameId":"g1","result":"ok","durationMs":1000,"bytes":500}`,
		`{"installId":"TEST-CI-02","event":"game_session","gameId":"g1","result":"ok","durationMs":60000}`,
	} {
		if w := submit(t, h, b); w.Code != http.StatusOK {
			t.Fatalf("submit %s -> %d %s", b, w.Code, w.Body.String())
		}
	}

	s := summary(t, h, "")
	if s.Totals.Events != 2 {
		t.Errorf("events = %d, want 2", s.Totals.Events)
	}
	if s.Totals.UniqueInstalls != 1 {
		t.Errorf("uniqueInstalls = %d, want 1", s.Totals.UniqueInstalls)
	}
	if s.Totals.UniquePlayers != 1 {
		t.Errorf("uniquePlayers = %d, want 1", s.Totals.UniquePlayers)
	}
	// Служебный прогон не должен приносить ни установок, ни трафика, ни
	// сессий: исключение сделано целиком, а не только для счёта уникальных.
	if s.Totals.Installs != 0 || s.Totals.BytesDownloaded != 0 {
		t.Errorf("installs = %d, bytes = %d, want 0/0", s.Totals.Installs, s.Totals.BytesDownloaded)
	}
	if len(s.ByGame) != 1 || s.ByGame[0].UniquePlayers != 1 || s.ByGame[0].Sessions != 1 {
		t.Errorf("byGame = %+v, want одну игру с одним игроком и одной сессией", s.ByGame)
	}
	for _, v := range s.AppVersion {
		if v.Key == "0.0.0-ci" {
			t.Errorf("версия автотеста попала в разбивку: %+v", s.AppVersion)
		}
	}
}

// Ошибка автотеста — не ошибка у пользователя, и в списке событий по коду ей
// делать нечего: иначе разбор сбоя начинается с чтения собственного прогона.
func TestSyntheticErrorsStayOutOfErrorEvents(t *testing.T) {
	h := New(t.TempDir())
	for _, b := range []string{
		`{"installId":"aaa","event":"error","gameId":"g1","errorCode":"sync_failed"}`,
		`{"installId":"test-ci-01","event":"error","gameId":"g1","errorCode":"sync_failed"}`,
	} {
		if w := submit(t, h, b); w.Code != http.StatusOK {
			t.Fatalf("submit %s -> %d", b, w.Code)
		}
	}

	code, out := errorEvents(t, h, "?code=sync_failed")
	if code != http.StatusOK {
		t.Fatalf("code = %d", code)
	}
	if len(out.Items) != 1 || out.Items[0].InstallID != "aaa" {
		t.Fatalf("got %+v, want только событие игрока", out.Items)
	}
}

// Событие всё-таки принимается и хранится: прогон, проверяющий приём, должен
// видеть 200, а не отказ — и отдельный счётчик показывает, что оно дошло.
func TestSyntheticEventIsStoredAndCountedApart(t *testing.T) {
	dir := t.TempDir()
	h := New(dir)
	p, reg := newProduct(t)
	h.Prom = p

	if w := submit(t, h, `{"installId":"test-ci-01","event":"game_launch","gameId":"g1"}`); w.Code != http.StatusOK {
		t.Fatalf("code = %d", w.Code)
	}

	out := dump(t, reg)
	mustContain(t, out, `chillhub_telemetry_synthetic_total{event="game_launch"} 1`)
	// Ищем именно строку-значение: HELP и TYPE объявлены для всех метрик и
	// без единого события.
	if strings.Contains(out, `chillhub_game_launches_total{`) {
		t.Errorf("прогон попал в продуктовый счётчик:\n%s", out)
	}
	if strings.Contains(out, `chillhub_telemetry_events_total{event="game_launch"}`) {
		t.Errorf("прогон попал в счётчик событий:\n%s", out)
	}
}

func TestIsSyntheticRecognisesOnlyTheReservedPrefix(t *testing.T) {
	for _, id := range []string{"test-ci-01", "TEST-CI-01", " test-x", "test-"} {
		if !isSynthetic(id) {
			t.Errorf("isSynthetic(%q) = false, want true", id)
		}
	}
	// Настоящий installId — GUID без дефисов; ни один из этих на служебный не
	// похож, и записать их в автотесты значило бы потерять живого игрока.
	for _, id := range []string{"", "aaa", "testing", "00d378defbff4348ab226f84361fec64", "user-test-1"} {
		if isSynthetic(id) {
			t.Errorf("isSynthetic(%q) = true, want false", id)
		}
	}
}

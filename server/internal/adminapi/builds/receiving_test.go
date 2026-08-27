package builds

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

// TestUploadStreamAnswersWhileTheArchiveIsStillArriving охраняет выкатку от
// таймаута Cloudflare.
//
// Перед доменом стоит прокси, у которого на первый байт ответа сто секунд.
// Раньше обработчик молчал, пока весь ZIP не ляжет во временный файл: 68 МБ
// сборки самообновления шли с раннера дольше, и выкатка получала 524 — шесть
// попыток подряд, каждая заново гнала весь архив. Поэтому отчёт о приёме
// обязан уходить ПОКА тело читается, а не после.
func TestUploadStreamAnswersWhileTheArchiveIsStillArriving(t *testing.T) {
	// Боевая выдержка — пять секунд; тесту нужно событие на первом же чтении.
	old := receivingEvery
	receivingEvery = 0
	t.Cleanup(func() { receivingEvery = old })

	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }

	w := httptest.NewRecorder()
	h.UploadStream(w, streamUploadRequest(t,
		map[string]string{"kind": "game", "gameId": "game", "version": "1.0.0"},
		zipBytes(t, map[string]string{"a.txt": "содержимое сборки"})))

	events, garbage := ndjsonEvents(t, w.Body.String())
	if len(garbage) > 0 {
		t.Fatalf("в потоке не-JSON: %v", garbage)
	}
	var receivingAt, savedAt = -1, -1
	for i, ev := range events {
		switch ev["type"] {
		case "receiving":
			if receivingAt < 0 {
				receivingAt = i
			}
		case "zipSaved":
			savedAt = i
		}
	}
	if receivingAt < 0 {
		t.Fatalf("о приёме архива не сообщено ни разу: %v", events)
	}
	if savedAt < 0 || receivingAt > savedAt {
		t.Errorf("отчёт о приёме пришёл не раньше сохранения архива: receiving=%d, zipSaved=%d", receivingAt, savedAt)
	}
}

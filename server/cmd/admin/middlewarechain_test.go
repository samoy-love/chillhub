package main

import (
	"bytes"
	"log"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"ChillHub/server/internal/httpx"
)

// adminHandler собирает боевую цепочку поверх боевых маршрутов.
func adminHandler(t *testing.T, corsOrigin string) http.Handler {
	t.Helper()
	s := newServer(t.TempDir())
	mux := http.NewServeMux()
	paths := s.register(mux)
	exact, prefixes := routeLabels(paths)
	return s.middleware(mux, corsOrigin, httpx.StaticRoutes(exact, prefixes))
}

// Preflight обязан получить ответ от CORS, а не 401 от авторизации: браузер,
// которому админку отдают с другого origin, до самого запроса тогда не
// доходит вовсе — при заданном ADMIN_CORS_ORIGIN админка просто не работала.
func TestPreflightIsAnsweredBeforeAuth(t *testing.T) {
	h := adminHandler(t, "https://admin.example.com")
	r := httptest.NewRequestWithContext(t.Context(), http.MethodOptions, "http://x/admin/api/games/list", nil)
	r.Header.Set("Origin", "https://admin.example.com")
	r.Header.Set("Access-Control-Request-Method", http.MethodGet)
	w := httptest.NewRecorder()
	h.ServeHTTP(w, r)
	if w.Code != http.StatusNoContent {
		t.Fatalf("preflight = %d, want 204 (%s)", w.Code, w.Body.String())
	}
	if got := w.Header().Get("Access-Control-Allow-Origin"); got != "https://admin.example.com" {
		t.Errorf("Access-Control-Allow-Origin = %q, want the requesting origin", got)
	}
}

// X-Request-Id, отданный клиенту, обязан стоять и в строке журнала: иначе
// «пришлите номер запроса» ничего не находит в логе. RequestID для этого
// должен быть СНАРУЖИ Logging.
func TestRequestIDReachesTheAccessLog(t *testing.T) {
	h := adminHandler(t, "none")
	var buf bytes.Buffer
	prevOut, prevFlags := log.Writer(), log.Flags()
	log.SetOutput(&buf)
	log.SetFlags(0)
	t.Cleanup(func() {
		log.SetOutput(prevOut)
		log.SetFlags(prevFlags)
	})

	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://x/admin/api/health", nil)
	w := httptest.NewRecorder()
	h.ServeHTTP(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("health = %d, want 200", w.Code)
	}
	rid := w.Header().Get("X-Request-Id")
	if rid == "" {
		t.Fatal("ответ ушёл без X-Request-Id")
	}
	if !strings.Contains(buf.String(), "reqid="+rid) {
		t.Fatalf("журнал не знает выданного номера %q:\n%s", rid, buf.String())
	}
}

// captureLog перехватывает журнал на время одного запроса.
func captureLog(t *testing.T) *bytes.Buffer {
	t.Helper()
	var buf bytes.Buffer
	prevOut, prevFlags := log.Writer(), log.Flags()
	log.SetOutput(&buf)
	log.SetFlags(0)
	t.Cleanup(func() {
		log.SetOutput(prevOut)
		log.SetFlags(prevFlags)
	})
	return &buf
}

// Отбитый авторизацией запрос обязан попасть в журнал доступа.
//
// Авторизация отвечает 401 сама и хендлер не зовёт, поэтому журнал, стоящий
// ВНУТРИ неё, такого запроса не видит вовсе. А это ровно один из двух случаев,
// ради которых в журнал и смотрят: «у меня истекла сессия» и сканер, который
// перебирает /admin/api/*. Счётчик их посчитает, но без пути и без номера.
func TestUnauthorizedRequestStillReachesTheAccessLog(t *testing.T) {
	h := adminHandler(t, "none")
	buf := captureLog(t)

	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://x/admin/api/games/list", nil)
	w := httptest.NewRecorder()
	h.ServeHTTP(w, r)

	if w.Code != http.StatusUnauthorized {
		t.Fatalf("без сессии = %d, want 401", w.Code)
	}
	if !strings.Contains(buf.String(), "/admin/api/games/list") {
		t.Fatalf("401 не попал в журнал: %s", buf.String())
	}
}

// То же для preflight: CORS отвечает 204 сам, и журнал обязан стоять снаружи него.
func TestPreflightStillReachesTheAccessLog(t *testing.T) {
	h := adminHandler(t, "https://admin.example.com")
	buf := captureLog(t)

	r := httptest.NewRequestWithContext(t.Context(), http.MethodOptions, "http://x/admin/api/games/list", nil)
	r.Header.Set("Origin", "https://admin.example.com")
	r.Header.Set("Access-Control-Request-Method", http.MethodGet)
	w := httptest.NewRecorder()
	h.ServeHTTP(w, r)

	if w.Code != http.StatusNoContent {
		t.Fatalf("preflight = %d, want 204", w.Code)
	}
	if !strings.Contains(buf.String(), "/admin/api/games/list") {
		t.Fatalf("preflight не попал в журнал: %s", buf.String())
	}
}

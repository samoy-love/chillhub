package main

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"ChillHub/server/internal/promexp"
)

func scrape(t *testing.T, s *server) string {
	t.Helper()
	var b strings.Builder
	if err := s.prom.reg.Write(&b); err != nil {
		t.Fatalf("Write: %v", err)
	}
	return b.String()
}

// TestExporterIsNotOnTheAdminMux — главная проверка закрытости: наружу торчит
// nginx, который проксирует ТОЛЬКО порт админки. Если экспортёр однажды
// окажется маршрутом этого же мультиплексора, продуктовые метрики поедут в
// интернет вместе с админкой.
func TestExporterIsNotOnTheAdminMux(t *testing.T) {
	s := testServer(t)
	mux := http.NewServeMux()
	paths := s.register(mux)

	for _, p := range paths {
		if strings.HasPrefix(p, "/internal/") || p == promexp.Path {
			t.Fatalf("экспортёр зарегистрирован на публичном мультиплексоре: %s", p)
		}
	}

	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, promexp.Path, nil))
	if rec.Code == http.StatusOK && strings.Contains(rec.Body.String(), "chillhub_") {
		t.Fatalf("%s отвечает метриками на порту админки:\n%s", promexp.Path, rec.Body.String())
	}
}

func TestFeedbackSubmitIsCounted(t *testing.T) {
	s := testServer(t)
	mux := http.NewServeMux()
	s.register(mux)

	rec := httptest.NewRecorder()
	mux.ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/feedback/submit",
		strings.NewReader(`{"message":"привет","category":"idea"}`)))

	out := scrape(t, s)
	want := `chillhub_feedback_submissions_total{result="ok"} 1`
	if rec.Code >= 400 {
		want = `chillhub_feedback_submissions_total{result="fail"} 1`
	}
	if !strings.Contains(out, want) {
		t.Fatalf("нет строки %q (код ответа %d) в:\n%s", want, rec.Code, out)
	}
}

// TestActivateAndMaintenanceAreCounted: обе операции меняют то, что видят все
// лаунчеры сразу, и обе обязаны быть отметками на оси времени.
func TestActivateAndMaintenanceAreCounted(t *testing.T) {
	s := testServer(t)
	mux := http.NewServeMux()
	s.register(mux)

	// Без авторизации оба вернут не-2xx — считаем именно это, метка result для
	// того и существует.
	mux.ServeHTTP(httptest.NewRecorder(), httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/activate?gameId=kitty&version=1.0.0", nil))
	mux.ServeHTTP(httptest.NewRecorder(), httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/maintenance/set", strings.NewReader(`{"enabled":true}`)))
	mux.ServeHTTP(httptest.NewRecorder(), httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/maintenance/clear", nil))

	out := scrape(t, s)
	for _, want := range []string{
		"chillhub_build_activations_total{result=",
		`chillhub_maintenance_changes_total{action="set",result=`,
		`chillhub_maintenance_changes_total{action="clear",result=`,
		"chillhub_maintenance_enabled 0",
	} {
		if !strings.Contains(out, want) {
			t.Fatalf("нет строки %q в:\n%s", want, out)
		}
	}
}

func TestRouteLabels(t *testing.T) {
	exact, prefixes := routeLabels([]string{"/admin/api/list", "/admin/ui/", "/news/"})
	if len(exact) != 1 || exact[0] != "/admin/api/list" {
		t.Fatalf("точные пути: %v", exact)
	}
	if len(prefixes) != 2 {
		t.Fatalf("префиксы: %v", prefixes)
	}
}

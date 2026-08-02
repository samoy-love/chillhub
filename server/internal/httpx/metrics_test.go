package httpx

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"ChillHub/server/internal/promexp"
)

func dump(t *testing.T, reg *promexp.Registry) string {
	t.Helper()
	var b strings.Builder
	if err := reg.Write(&b); err != nil {
		t.Fatalf("Write: %v", err)
	}
	return b.String()
}

func TestMetricsCountsCodesAndRoutes(t *testing.T) {
	reg := promexp.New()
	routes := StaticRoutes([]string{"/api/games"}, []string{"/content/"})
	h := Metrics(reg, "api", routes)(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/content/x.pak" {
			w.WriteHeader(http.StatusNotFound)
			return
		}
		w.WriteHeader(http.StatusOK)
	}))

	for _, p := range []string{"/api/games", "/api/games", "/content/x.pak", "/whatever"} {
		h.ServeHTTP(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, p, nil))
	}

	out := dump(t, reg)
	for _, want := range []string{
		`chillhub_http_requests_total{service="api",route="/api/games",method="GET",code="200"} 2`,
		`chillhub_http_requests_total{service="api",route="/content/",method="GET",code="404"} 1`,
		`chillhub_http_requests_total{service="api",route="other",method="GET",code="200"} 1`,
		`chillhub_http_request_duration_seconds_count{service="api",route="/api/games"} 2`,
	} {
		if !strings.Contains(out, want) {
			t.Fatalf("нет строки %q в:\n%s", want, out)
		}
	}
}

// TestMetricsFoldsUnknownPathsAndMethods: путь и метод задаёт клиент, и ни то
// ни другое не должно уметь плодить ряды в TSDB.
func TestMetricsFoldsUnknownPathsAndMethods(t *testing.T) {
	reg := promexp.New()
	h := Metrics(reg, "api", StaticRoutes(nil, nil))(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	for i := 0; i < 50; i++ {
		h.ServeHTTP(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, "/"+strings.Repeat("a", i+1), nil))
	}
	r := httptest.NewRequest(http.MethodGet, "/x", nil)
	r.Method = "TRACE"
	h.ServeHTTP(httptest.NewRecorder(), r)

	out := dump(t, reg)
	if !strings.Contains(out, `route="other",method="GET",code="200"} 50`) {
		t.Fatalf("пути не свёрнуты:\n%s", out)
	}
	if !strings.Contains(out, `method="other"`) {
		t.Fatalf("нестандартный метод не свёрнут:\n%s", out)
	}
}

package promexp

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func render(t *testing.T, r *Registry) string {
	t.Helper()
	var b strings.Builder
	if err := r.Write(&b); err != nil {
		t.Fatalf("Write: %v", err)
	}
	return b.String()
}

func TestCounterFormat(t *testing.T) {
	r := New()
	c := r.NewCounter("chillhub_installs_total", "Установки", "game", "result")
	c.Inc("kitty", "ok")
	c.Inc("kitty", "ok")
	c.Inc("kitty", "fail")

	got := render(t, r)
	want := "# HELP chillhub_installs_total Установки\n" +
		"# TYPE chillhub_installs_total counter\n" +
		`chillhub_installs_total{game="kitty",result="fail"} 1` + "\n" +
		`chillhub_installs_total{game="kitty",result="ok"} 2` + "\n"
	if got != want {
		t.Fatalf("вывод не совпал:\n--- got ---\n%s\n--- want ---\n%s", got, want)
	}
}

func TestCounterIgnoresNegative(t *testing.T) {
	// rate() reads a decrease as a counter reset, i.e. as a restart that never
	// happened — the graph would show a spike out of nowhere.
	r := New()
	c := r.NewCounter("x_total", "x")
	c.Add(5)
	c.Add(-3)
	if !strings.Contains(render(t, r), "x_total 5\n") {
		t.Fatalf("отрицательное приращение изменило счётчик:\n%s", render(t, r))
	}
}

func TestGaugeAndGaugeFunc(t *testing.T) {
	r := New()
	g := r.NewGauge("temp", "Температура", "where")
	g.Set(1.5, "here")
	g.Set(-2, "there")
	on := true
	r.NewGaugeFunc("flag", "Флаг", func() float64 {
		if on {
			return 1
		}
		return 0
	})
	out := render(t, r)
	for _, want := range []string{`temp{where="here"} 1.5`, `temp{where="there"} -2`, "flag 1"} {
		if !strings.Contains(out, want) {
			t.Fatalf("нет строки %q в:\n%s", want, out)
		}
	}
	on = false
	if !strings.Contains(render(t, r), "flag 0") {
		t.Fatal("GaugeFunc не перечитывает значение на каждом scrape")
	}
}

func TestHistogramBucketsAreCumulative(t *testing.T) {
	r := New()
	h := r.NewHistogram("dur_seconds", "Длительность", []float64{1, 2}, "route")
	h.Observe(0.5, "/a")
	h.Observe(1.5, "/a")
	h.Observe(9, "/a")

	out := render(t, r)
	for _, want := range []string{
		`dur_seconds_bucket{route="/a",le="1"} 1`,
		`dur_seconds_bucket{route="/a",le="2"} 2`,
		`dur_seconds_bucket{route="/a",le="+Inf"} 3`,
		`dur_seconds_sum{route="/a"} 11`,
		`dur_seconds_count{route="/a"} 3`,
	} {
		if !strings.Contains(out, want) {
			t.Fatalf("нет строки %q в:\n%s", want, out)
		}
	}
}

func TestLabelValuesAreEscaped(t *testing.T) {
	// Label values come from the launcher; a quote or a newline in one of them
	// would otherwise forge samples in the exposition document.
	r := New()
	c := r.NewCounter("e_total", "e", "code")
	c.Inc(`sync"failed` + "\n" + `x\y`)
	out := render(t, r)
	want := `e_total{code="sync\"failed\nx\\y"} 1`
	if !strings.Contains(out, want) {
		t.Fatalf("значение метки не экранировано:\n%s", out)
	}
	if strings.Count(out, "\n") != 3 {
		t.Fatalf("перевод строки в метке породил лишние строки:\n%s", out)
	}
}

func TestCardinalityIsCapped(t *testing.T) {
	r := New()
	c := r.NewCounter("g_total", "g", "game")
	for i := range MaxSeries + 50 {
		c.Inc(strings.Repeat("a", i+1))
	}
	out := render(t, r)
	// MaxSeries обычных рядов плюс один сборный "other".
	if n := strings.Count(out, "g_total{"); n != MaxSeries+1 {
		t.Fatalf("рядов %d, ожидалось %d", n, MaxSeries+1)
	}
	if !strings.Contains(out, `g_total{game="other"} 50`) {
		t.Fatalf("переполнение не свёрнуто в %q:\n%s", OverflowValue, out)
	}
}

func TestWrongLabelArityDoesNotPanic(t *testing.T) {
	r := New()
	c := r.NewCounter("w_total", "w", "a", "b")
	c.Inc("only-one")
	if !strings.Contains(render(t, r), `w_total{a="other",b="other"} 1`) {
		t.Fatalf("наблюдение с неверным числом меток потеряно:\n%s", render(t, r))
	}
}

func TestDuplicateNamePanics(t *testing.T) {
	defer func() {
		if recover() == nil {
			t.Fatal("повторная регистрация имени не привела к panic")
		}
	}()
	r := New()
	r.NewCounter("dup_total", "d")
	r.NewCounter("dup_total", "d")
}

func TestHandler(t *testing.T) {
	r := New()
	r.NewCounter("h_total", "h").Inc()

	rec := httptest.NewRecorder()
	r.Handler().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodGet, Path, nil))
	if rec.Code != http.StatusOK {
		t.Fatalf("код %d", rec.Code)
	}
	if ct := rec.Header().Get("Content-Type"); ct != ContentType {
		t.Fatalf("Content-Type = %q", ct)
	}
	if !strings.Contains(rec.Body.String(), "h_total 1") {
		t.Fatalf("тело: %s", rec.Body.String())
	}

	rec = httptest.NewRecorder()
	r.Handler().ServeHTTP(rec, httptest.NewRequestWithContext(t.Context(), http.MethodPost, Path, nil))
	if rec.Code != http.StatusMethodNotAllowed {
		t.Fatalf("POST на экспортёр вернул %d", rec.Code)
	}
}

func TestIsLoopback(t *testing.T) {
	cases := map[string]bool{
		"127.0.0.1:9101": true,
		"localhost:9101": true,
		"[::1]:9101":     true,
		"0.0.0.0:9101":   false,
		":9101":          false,
		"172.17.0.1:910": false,
		"broken":         false,
	}
	for addr, want := range cases {
		if got := isLoopback(addr); got != want {
			t.Errorf("isLoopback(%q) = %v, ожидалось %v", addr, got, want)
		}
	}
}

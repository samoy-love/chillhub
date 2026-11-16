package auth

import (
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
)

func TestLoginRejectsPartialOrQueryCredentials(t *testing.T) {
	a, pass := newTestAuth(t)
	query := url.Values{"username": {"admin"}, "password": {pass}}.Encode()
	body := `{"username":"admin","password":"` + pass + `"}`
	for _, tc := range []struct{ name, contentType, suffix, body string }{
		{"bad field after credentials", "application/json", "", strings.TrimSuffix(body, "}") + `,"username":1}`},
		{"trailing JSON", "application/json", "", body + ` {}`},
		{"trailing oversized data", "application/json", "", body + strings.Repeat(" ", maxLoginBodyBytes)},
		{"malformed form", "application/x-www-form-urlencoded", "", query + "&bad=%"},
		{"query only", "application/x-www-form-urlencoded", "?" + query, ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			r := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/auth/login"+tc.suffix, strings.NewReader(tc.body))
			r.Header.Set("Content-Type", tc.contentType)
			w := httptest.NewRecorder()
			a.HandleLogin(w, r)
			if w.Code != http.StatusBadRequest || w.Header().Get("Set-Cookie") != "" {
				t.Fatalf("status = %d, want 400 without a session", w.Code)
			}
		})
	}
}

// countingReader считает, сколько байт тела действительно вычитал обработчик.
type countingReader struct {
	r io.Reader
	n int64
}

func (c *countingReader) Read(p []byte) (int, error) {
	n, err := c.r.Read(p)
	c.n += int64(n)
	return n, err
}

// Вход — единственная ручка админки без авторизации, и до потолка на тело
// декодер буферизовал ровно столько, сколько прислали. Проверяем не код ответа
// (он и раньше был не 200), а то, что обработчик перестал читать: иначе
// «попытка входа» на гигабайт съедала память процесса, который заодно
// обслуживает публичные /feedback/submit и /metrics/report.
func TestLoginStopsReadingAnOversizedBody(t *testing.T) {
	a, _ := newTestAuth(t)
	for _, c := range []struct{ what, contentType, prefix string }{
		{"json", "application/json", `{"username":"admin","password":"`},
		{"форма", "application/x-www-form-urlencoded", "username=admin&password="},
	} {
		t.Run(c.what, func(t *testing.T) {
			body := &countingReader{r: io.MultiReader(
				strings.NewReader(c.prefix),
				io.LimitReader(neverEndingReader{}, 64<<20),
			)}
			r := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "http://x/admin/api/login", body)
			r.Header.Set("Content-Type", c.contentType)
			w := httptest.NewRecorder()
			a.HandleLogin(w, r)
			if w.Code == http.StatusOK {
				t.Fatalf("вход по обрезанному телу удался: %s", w.Body.String())
			}
			if body.n > maxLoginBodyBytes+(4<<10) {
				t.Fatalf("прочитано %d байт тела при потолке %d", body.n, maxLoginBodyBytes)
			}
		})
	}
}

// neverEndingReader отдаёт байты, пока их берут.
type neverEndingReader struct{}

func (neverEndingReader) Read(p []byte) (int, error) {
	for i := range p {
		p[i] = 'A'
	}
	return len(p), nil
}

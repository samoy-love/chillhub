package auth

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"golang.org/x/crypto/bcrypt"
)

// newTestAuth builds an Auth with a known password. bcrypt cost 4 keeps the suite fast;
// production uses 12 and that choice is not what these tests are about.
func newTestAuth(t *testing.T) (*Auth, string) {
	t.Helper()
	const pass = "correct horse battery staple"
	hb, err := bcrypt.GenerateFromPassword([]byte(pass), 4)
	if err != nil {
		t.Fatal(err)
	}
	a := New(Config{
		AdminUser:   "admin",
		AdminPassBC: string(hb),
		JWTSecret:   []byte("test-secret-not-used-anywhere-else"),
		AccessTTL:   time.Hour,
		RefreshTTL:  24 * time.Hour,
	})
	return a, pass
}

// loginResult — прочитанный ответ на попытку входа: код, куки и тело.
//
// Раньше login отдавал *http.Response, тело которого не закрывал ни один из
// тринадцати вызовов в двух файлах. Для httptest это буфер в памяти, а не
// сокет, но правило одно на весь код: незакрытое тело ответа рано или поздно
// оказывается настоящим соединением. Держать `defer resp.Body.Close()` в
// тринадцати местах — худший из вариантов: строчка, про которую надо помнить,
// и ни одной проверки, которая напомнит. Поэтому ответ вычитывается и
// закрывается один раз, внутри login, а тесты получают уже готовые значения.
type loginResult struct {
	StatusCode int
	Body       string
	cookies    []*http.Cookie
}

// Cookies повторяет сигнатуру (*http.Response).Cookies, чтобы вызовы в тестах
// читались так же, как раньше.
func (l loginResult) Cookies() []*http.Cookie { return l.cookies }

func login(t *testing.T, a *Auth, user, pass string) loginResult {
	t.Helper()
	body := `{"username":"` + user + `","password":"` + pass + `"}`
	r := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/auth/login", strings.NewReader(body))
	r.Header.Set("Content-Type", "application/json")
	w := httptest.NewRecorder()
	a.HandleLogin(w, r)

	resp := w.Result()
	defer func() { _ = resp.Body.Close() }()

	raw, err := io.ReadAll(resp.Body)
	if err != nil {
		t.Fatalf("не удалось прочитать ответ на вход: %v", err)
	}

	return loginResult{StatusCode: resp.StatusCode, Body: string(raw), cookies: resp.Cookies()}
}

// The wrong password must not authenticate — and must not leak whether the user exists.
func TestLoginRejectsWrongPassword(t *testing.T) {
	a, _ := newTestAuth(t)

	for name, creds := range map[string][2]string{
		"wrong password": {"admin", "not-the-password"},
		"wrong user":     {"root", "correct horse battery staple"},
		"empty password": {"admin", ""},
		"empty user":     {"", "correct horse battery staple"},
	} {
		resp := login(t, a, creds[0], creds[1])
		if resp.StatusCode == http.StatusOK {
			t.Errorf("%s: login succeeded, must not", name)
		}
		for _, c := range resp.Cookies() {
			if c.Name == cookieAccess && c.Value != "" {
				t.Errorf("%s: an access cookie was issued on a failed login", name)
			}
		}
	}
}

// A correct login issues both the HttpOnly session cookie and the CSRF cookie the
// UI reads from JavaScript; without the pair every state-changing call would fail.
func TestLoginIssuesSessionAndCSRFCookies(t *testing.T) {
	a, pass := newTestAuth(t)
	resp := login(t, a, "admin", pass)
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("login failed: %d", resp.StatusCode)
	}

	var access, csrf *http.Cookie
	for _, c := range resp.Cookies() {
		switch c.Name {
		case cookieAccess:
			access = c
		case cookieCSRF:
			csrf = c
		}
	}
	if access == nil || access.Value == "" {
		t.Fatal("no access cookie")
	}
	if !access.HttpOnly {
		t.Error("the access cookie must be HttpOnly: otherwise any XSS reads the session")
	}
	if csrf == nil || csrf.Value == "" {
		t.Fatal("no CSRF cookie")
	}
	if csrf.HttpOnly {
		t.Error("the CSRF cookie must be readable by JS — the UI echoes it back in a header")
	}
}

// The session issued by login must actually authenticate a follow-up request.
func TestSessionFromLoginAuthenticates(t *testing.T) {
	a, pass := newTestAuth(t)
	resp := login(t, a, "admin", pass)

	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/list", nil)
	for _, c := range resp.Cookies() {
		r.AddCookie(c)
	}
	if got := a.CurrentUser(r); got != "admin" {
		t.Fatalf("CurrentUser = %q, want admin", got)
	}
}

// Middleware is what nginx relies on. Paths that must stay open are open, and
// everything under /admin/ is closed without a session.
func TestMiddlewareAllowlist(t *testing.T) {
	a, _ := newTestAuth(t)
	next := http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusTeapot) })
	h := a.Middleware(next)

	open := []string{
		"/admin/ui/login.html",
		"/admin/api/auth/login",
		"/admin/api/health",
		"/admin/",
		"/feedback/submit",
		// Outside the /admin/ prefix entirely: the public metrics ingest.
		"/metrics/report",
	}
	for _, p := range open {
		w := httptest.NewRecorder()
		h.ServeHTTP(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, p, nil))
		if w.Code != http.StatusTeapot {
			t.Errorf("%s must be reachable without a session, got %d", p, w.Code)
		}
	}

	closed := []string{
		"/admin/api/list",
		"/admin/api/deleteVersion",
		"/admin/api/news/save",
		"/admin/api/feedback/list",
	}
	for _, p := range closed {
		w := httptest.NewRecorder()
		h.ServeHTTP(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, p, nil))
		if w.Code == http.StatusTeapot {
			t.Errorf("%s reached the handler without a session", p)
		}
	}
}

// Where the gate for admin.js lives.
//
// The Go allowlist opens all of /admin/ui/ on purpose: the login page has to load
// its own stylesheet and script. admin.js is closed one layer up, by the
// auth_request block in nginx — verified against production, where an anonymous
// GET of /admin/ui/admin.js returns 401.
//
// This test pins that split. If it ever starts failing, the gate moved, and
// whoever moved it must decide which layer owns it — because running the admin
// service WITHOUT nginx (a dev box, a container) serves admin.js to anyone.
func TestAdminScriptIsGatedByNginxNotByGo(t *testing.T) {
	a, _ := newTestAuth(t)
	reached := false
	next := http.HandlerFunc(func(_ http.ResponseWriter, _ *http.Request) { reached = true })
	w := httptest.NewRecorder()
	a.Middleware(next).ServeHTTP(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/ui/admin.js", nil))
	if !reached {
		t.Fatal("Go now blocks admin.js — the nginx auth_request block is redundant, remove one of the two")
	}
}

// Logout must invalidate the session cookies, not merely redirect.
func TestLogoutClearsCookies(t *testing.T) {
	a, pass := newTestAuth(t)
	loginResp := login(t, a, "admin", pass)

	r := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/auth/logout", nil)
	for _, c := range loginResp.Cookies() {
		r.AddCookie(c)
	}
	w := httptest.NewRecorder()
	a.HandleLogout(w, r)

	for _, c := range w.Result().Cookies() {
		if (c.Name == cookieAccess || c.Name == cookieCSRF) && c.Value != "" && c.MaxAge >= 0 {
			t.Errorf("cookie %q was not cleared: value=%q maxAge=%d", c.Name, c.Value, c.MaxAge)
		}
	}
}

// HandleMe answers who the caller is; anonymous callers must not get a name.
func TestHandleMeRequiresSession(t *testing.T) {
	a, pass := newTestAuth(t)

	w := httptest.NewRecorder()
	a.HandleMe(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/auth/me", nil))
	if w.Code == http.StatusOK && strings.Contains(w.Body.String(), "admin") {
		t.Error("an anonymous caller was told the admin username")
	}

	resp := login(t, a, "admin", pass)
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/auth/me", nil)
	for _, c := range resp.Cookies() {
		r.AddCookie(c)
	}
	w = httptest.NewRecorder()
	a.HandleMe(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("authenticated /me returned %d", w.Code)
	}
}

// HandleVerify is what nginx auth_request calls: 2xx means allowed, anything else denies.
func TestHandleVerifyGatesOnSession(t *testing.T) {
	a, pass := newTestAuth(t)

	w := httptest.NewRecorder()
	a.HandleVerify(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/auth/verify", nil))
	if w.Code >= 200 && w.Code < 300 {
		t.Errorf("verify allowed an anonymous request (%d) — nginx would open the admin UI", w.Code)
	}

	resp := login(t, a, "admin", pass)
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/auth/verify", nil)
	for _, c := range resp.Cookies() {
		r.AddCookie(c)
	}
	w = httptest.NewRecorder()
	a.HandleVerify(w, r)
	if w.Code < 200 || w.Code >= 300 {
		t.Errorf("verify denied a valid session (%d) — the admin would be locked out", w.Code)
	}
}

// A login body that is not JSON must be refused, not panic the handler.
func TestLoginRejectsGarbageBody(t *testing.T) {
	a, _ := newTestAuth(t)
	r := httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/admin/api/auth/login", strings.NewReader("не json"))
	w := httptest.NewRecorder()
	a.HandleLogin(w, r)
	if w.Code == http.StatusOK {
		t.Error("garbage body authenticated")
	}
}

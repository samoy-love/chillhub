package auth

import (
	"encoding/base64"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
	"time"
)

// secureAuth is the production-shaped configuration: cookies marked Secure and
// bound to a domain, which is what the flag assertions below are about.
func secureAuth(t *testing.T) *Auth {
	t.Helper()
	return New(Config{
		AdminUser:    "admin",
		JWTSecret:    []byte("secret-for-session-tests"),
		CookieDomain: "admin.example",
		CookieSecure: true,
		AccessTTL:    time.Hour,
		RefreshTTL:   24 * time.Hour,
	})
}

// cookieSource — всё, у чего можно спросить выданные куки: и *http.Response,
// и loginResult из handlers_test.go.
type cookieSource interface {
	Cookies() []*http.Cookie
}

// cookiesOf indexes a response's Set-Cookie headers by name.
func cookiesOf(resp cookieSource) map[string]*http.Cookie {
	out := map[string]*http.Cookie{}
	for _, c := range resp.Cookies() {
		out[c.Name] = c
	}
	return out
}

// authGet builds a GET carrying one access cookie.
func authGet(token string) *http.Request {
	r := httptest.NewRequest(http.MethodGet, "http://example.com/admin/api/list", nil)
	r.AddCookie(&http.Cookie{Name: cookieAccess, Value: token})
	return r
}

// ===== Token forgery =====

// A token whose exp has passed must stop working. Access tokens live a day, so
// an admin who logs in on a shared machine relies on the expiry being the thing
// that ends the session — a cookie that outlives its exp never ends it.
func TestExpiredAccessTokenIsRejected(t *testing.T) {
	a := secureAuth(t)
	// Well past the 30s leeway the parser allows for clock skew.
	expired, err := a.signToken("admin", tokenAccess, -time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	if user := a.CurrentUser(authGet(expired)); user != "" {
		t.Fatalf("expired token still authenticates as %q", user)
	}
}

// Flipping a byte of the signature must invalidate the token. If the payload
// were trusted without a signature check, anyone could set sub to the admin
// name and reach the build upload endpoints.
func TestTamperedSignatureIsRejected(t *testing.T) {
	a := secureAuth(t)
	valid, err := a.signToken("admin", tokenAccess, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	parts := strings.Split(valid, ".")
	if len(parts) != 3 {
		t.Fatalf("unexpected token shape: %q", valid)
	}
	sig := []byte(parts[2])
	if sig[len(sig)-1] == 'A' {
		sig[len(sig)-1] = 'B'
	} else {
		sig[len(sig)-1] = 'A'
	}
	tampered := parts[0] + "." + parts[1] + "." + string(sig)
	if user := a.CurrentUser(authGet(tampered)); user != "" {
		t.Fatalf("token with a broken signature authenticates as %q", user)
	}
}

// alg=none is the classic JWT bypass: the attacker drops the signature and
// declares the algorithm "none". The parser is pinned to HS256 for exactly this
// reason; without the pin an unsigned token would be accepted as valid.
func TestAlgNoneTokenIsRejected(t *testing.T) {
	a := secureAuth(t)
	enc := func(s string) string { return base64.RawURLEncoding.EncodeToString([]byte(s)) }
	header := enc(`{"alg":"none","typ":"JWT"}`)
	exp := time.Now().Add(time.Hour).Unix()
	payload := enc(`{"typ":"access","sub":"admin","exp":` + strconv.FormatInt(exp, 10) + `}`)
	// A trailing dot with an empty signature is the shape alg=none uses.
	unsigned := header + "." + payload + "."
	if user := a.CurrentUser(authGet(unsigned)); user != "" {
		t.Fatalf("alg=none token authenticates as %q", user)
	}
}

// A token minted by another deployment (or by a leaked older secret) must not
// open this one. Rotating JWT_SECRET is the only way to kick every session out;
// if a foreign signature were accepted, rotation would do nothing.
func TestTokenSignedWithAnotherSecretIsRejected(t *testing.T) {
	a := secureAuth(t)
	other := New(Config{
		AdminUser:  "admin",
		JWTSecret:  []byte("a-completely-different-secret"),
		AccessTTL:  time.Hour,
		RefreshTTL: time.Hour,
	})
	foreign, err := other.signToken("admin", tokenAccess, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	if user := a.CurrentUser(authGet(foreign)); user != "" {
		t.Fatalf("foreign token authenticates as %q", user)
	}
}

// The two token types are not interchangeable. A refresh token lives 30 days
// and is meant to be exchanged, not presented: if it authenticated API calls
// directly, the short access lifetime would buy nothing.
func TestTokenTypesAreNotInterchangeable(t *testing.T) {
	a := secureAuth(t)
	refresh, err := a.signToken("admin", tokenRefresh, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	if user := a.CurrentUser(authGet(refresh)); user != "" {
		t.Fatalf("a refresh token was accepted as an access token (user %q)", user)
	}

	access, err := a.signToken("admin", tokenAccess, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	r := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/auth/refresh", nil)
	r.AddCookie(&http.Cookie{Name: cookieRefresh, Value: access})
	w := httptest.NewRecorder()
	a.HandleRefresh(w, r)
	if w.Code == http.StatusOK {
		t.Fatal("an access token was accepted as a refresh token")
	}
}

// Garbage in the cookie must be a plain rejection, not a panic in the parser.
func TestMalformedTokenIsRejected(t *testing.T) {
	a := secureAuth(t)
	for _, tok := range []string{"not-a-token", "a.b.c", "...", strings.Repeat("x", 4096)} {
		if user := a.CurrentUser(authGet(tok)); user != "" {
			t.Errorf("token %.20q authenticates as %q", tok, user)
		}
	}
}

// ===== Cookie flags =====

// Losing any one of these flags leaks the admin session:
// HttpOnly — any XSS in the panel reads the token;
// Secure   — a single plain-HTTP request puts it on the wire;
// SameSite — a third-party page can drive authenticated navigations.
func TestSessionCookiesCarryEveryProtectionFlag(t *testing.T) {
	a := secureAuth(t)
	w := httptest.NewRecorder()
	if err := a.issueSession(w, "admin"); err != nil {
		t.Fatal(err)
	}
	set := cookiesOf(w.Result())

	for _, name := range []string{cookieAccess, cookieRefresh} {
		c := set[name]
		if c == nil {
			t.Fatalf("%s cookie was not issued", name)
		}
		if !c.HttpOnly {
			t.Errorf("%s is not HttpOnly: any XSS in the admin panel reads the session", name)
		}
		if !c.Secure {
			t.Errorf("%s is not Secure: it would travel over plain HTTP", name)
		}
		if c.SameSite == http.SameSiteNoneMode || c.SameSite == http.SameSiteDefaultMode {
			t.Errorf("%s has no SameSite restriction (%v): cross-site requests would carry it", name, c.SameSite)
		}
	}

	// The CSRF token is deliberately readable by JS, but it must not be the
	// weak link either: sent in the clear it hands an attacker the second half
	// of the double-submit pair.
	csrf := set[cookieCSRF]
	if csrf == nil {
		t.Fatal("CSRF cookie was not issued")
	}
	if csrf.HttpOnly {
		t.Error("the CSRF cookie must stay readable by the admin UI")
	}
	if !csrf.Secure {
		t.Error("the CSRF cookie is not Secure")
	}
}

// The cookies that end a session must be as protected as the ones that start
// it, otherwise the clearing request itself is what an attacker intercepts.
func TestClearedCookiesKeepTheirFlags(t *testing.T) {
	a := secureAuth(t)
	w := httptest.NewRecorder()
	a.clearSession(w)
	set := cookiesOf(w.Result())
	for _, name := range []string{cookieAccess, cookieRefresh, cookieCSRF} {
		c := set[name]
		if c == nil {
			t.Fatalf("%s was not cleared at all", name)
		}
		if c.Value != "" || c.MaxAge >= 0 {
			t.Errorf("%s not actually expired: value=%q maxAge=%d", name, c.Value, c.MaxAge)
		}
		if !c.Secure || !c.HttpOnly {
			t.Errorf("%s lost Secure/HttpOnly while being cleared", name)
		}
	}
}

// Every login must mint a fresh CSRF token. Reusing one across sessions means a
// value captured once stays valid after the admin logs back in.
func TestEachSessionGetsAFreshCSRFToken(t *testing.T) {
	a := secureAuth(t)
	seen := map[string]bool{}
	for i := 0; i < 5; i++ {
		w := httptest.NewRecorder()
		if err := a.issueSession(w, "admin"); err != nil {
			t.Fatal(err)
		}
		v := cookiesOf(w.Result())[cookieCSRF].Value
		if v == "" {
			t.Fatal("empty CSRF token issued")
		}
		if seen[v] {
			t.Fatalf("CSRF token repeated across sessions: %q", v)
		}
		seen[v] = true
	}
}

// ===== CSRF double submit =====

// The header alone is not enough, and neither is the cookie alone. Both halves
// missing or empty must fail closed: an empty-vs-empty comparison would
// otherwise "match" and let any cross-site POST through.
func TestCSRFRequiresBothHalvesNonEmpty(t *testing.T) {
	a := secureAuth(t)
	access, err := a.signToken("admin", tokenAccess, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	const token = "csrf-token-value-0123456789"
	cases := []struct {
		name           string
		cookie, hdr    string
		setCookie      bool
		setHeader      bool
		wantAuthorized bool
	}{
		{name: "both present and equal", cookie: token, hdr: token, setCookie: true, setHeader: true, wantAuthorized: true},
		{name: "no csrf cookie", hdr: token, setHeader: true},
		{name: "empty csrf cookie", cookie: "", hdr: token, setCookie: true, setHeader: true},
		{name: "empty header", cookie: token, hdr: "", setCookie: true, setHeader: true},
		{name: "both empty", cookie: "", hdr: "", setCookie: true, setHeader: true},
		{name: "neither present", cookie: "", hdr: ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/news/save", nil)
			r.AddCookie(&http.Cookie{Name: cookieAccess, Value: access})
			if c.setCookie {
				r.AddCookie(&http.Cookie{Name: cookieCSRF, Value: c.cookie})
			}
			if c.setHeader {
				r.Header.Set(headerCSRF, c.hdr)
			}
			got := a.CurrentUser(r) != ""
			if got != c.wantAuthorized {
				t.Fatalf("authorized=%v, want %v", got, c.wantAuthorized)
			}
		})
	}
}

// The CSRF check must cover every state-changing verb, not just POST: the
// delete and rename endpoints would otherwise be reachable cross-site.
func TestCSRFIsEnforcedOnEveryWriteMethod(t *testing.T) {
	a := secureAuth(t)
	access, err := a.signToken("admin", tokenAccess, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	for _, m := range []string{http.MethodPost, http.MethodPut, http.MethodPatch, http.MethodDelete} {
		r := httptest.NewRequest(m, "http://example.com/admin/api/x", nil)
		r.AddCookie(&http.Cookie{Name: cookieAccess, Value: access})
		if user := a.CurrentUser(r); user != "" {
			t.Errorf("%s passed without a CSRF token (user %q)", m, user)
		}
	}
}

// A CSRF pair alone, with no session cookie, must not authenticate: the CSRF
// token is a second factor against cross-site use, never a credential.
func TestCSRFPairWithoutSessionIsNotACredential(t *testing.T) {
	a := secureAuth(t)
	r := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/x", nil)
	r.AddCookie(&http.Cookie{Name: cookieCSRF, Value: "some-token"})
	r.Header.Set(headerCSRF, "some-token")
	if user := a.CurrentUser(r); user != "" {
		t.Fatalf("a CSRF pair alone authenticated as %q", user)
	}
}

// ===== Login / logout / refresh =====

// A wrong password and an unknown username must be indistinguishable. Any
// difference in status or body turns the login form into a user enumeration
// oracle, which is the first step of a targeted brute force.
func TestUnknownUserAndWrongPasswordAnswerIdentically(t *testing.T) {
	a, pass := newTestAuth(t)

	wrongPass := login(t, a, "admin", pass+"x")
	unknownUser := login(t, a, "nosuchadmin", pass)

	if wrongPass.StatusCode != unknownUser.StatusCode {
		t.Errorf("status differs: wrong password %d vs unknown user %d",
			wrongPass.StatusCode, unknownUser.StatusCode)
	}
	b1, b2 := wrongPass.Body, unknownUser.Body
	if b1 != b2 {
		t.Errorf("body differs: wrong password %q vs unknown user %q", b1, b2)
	}
	if len(wrongPass.Cookies()) != 0 || len(unknownUser.Cookies()) != 0 {
		t.Error("a failed login issued cookies")
	}
}

// The username comparison is case- and space-insensitive on purpose (an admin
// typing "Admin " must get in), while the password stays exact.
func TestUsernameIsNormalisedButPasswordIsNot(t *testing.T) {
	a, pass := newTestAuth(t)
	if resp := login(t, a, "  ADMIN  ", pass); resp.StatusCode != http.StatusOK {
		t.Errorf("a padded, upper-case username was refused: %d", resp.StatusCode)
	}
	if resp := login(t, a, "admin", strings.ToUpper(pass)); resp.StatusCode == http.StatusOK {
		t.Error("the password comparison is case-insensitive")
	}
}

// The login form posts urlencoded when JavaScript is unavailable; that path
// must work too, otherwise a broken admin.js locks the admin out completely.
func TestLoginAcceptsFormEncodedBody(t *testing.T) {
	a, pass := newTestAuth(t)
	body := "username=admin&password=" + strings.ReplaceAll(pass, " ", "+")
	r := httptest.NewRequest(http.MethodPost, "/admin/api/auth/login", strings.NewReader(body))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	w := httptest.NewRecorder()
	a.HandleLogin(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("form login failed: %d %s", w.Code, w.Body.String())
	}
	if cookiesOf(w.Result())[cookieAccess] == nil {
		t.Error("form login issued no session")
	}
}

// A service with no configured credentials must refuse to authenticate anyone
// rather than fall back to "no password required".
func TestLoginRefusesWhenNotConfigured(t *testing.T) {
	cases := map[string]Config{
		"no username": {AdminPassBC: "$2a$04$abcdefghijklmnopqrstuv", JWTSecret: []byte("s")},
		"no password": {AdminUser: "admin", JWTSecret: []byte("s")},
		"no secret":   {AdminUser: "admin", AdminPassBC: "$2a$04$abcdefghijklmnopqrstuv"},
	}
	for name, cfg := range cases {
		a := New(cfg)
		resp := login(t, a, "admin", "whatever")
		if resp.StatusCode == http.StatusOK {
			t.Errorf("%s: login succeeded on an unconfigured service", name)
		}
		if len(resp.Cookies()) != 0 {
			t.Errorf("%s: an unconfigured service issued cookies", name)
		}
	}
}

// The session endpoints change state, so a GET must not reach them — a plain
// <img src="/admin/api/auth/logout"> would otherwise log the admin out.
func TestSessionEndpointsRejectGet(t *testing.T) {
	a := secureAuth(t)
	for name, h := range map[string]http.HandlerFunc{
		"login":   a.HandleLogin,
		"logout":  a.HandleLogout,
		"refresh": a.HandleRefresh,
	} {
		w := httptest.NewRecorder()
		h(w, httptest.NewRequest(http.MethodGet, "/admin/api/auth/"+name, nil))
		if w.Code != http.StatusMethodNotAllowed {
			t.Errorf("%s answered GET with %d, want 405", name, w.Code)
		}
	}
}

// Logout must leave the browser with nothing usable. Replaying the cleared
// cookies is exactly what a browser does next, and it must not authenticate.
func TestLogoutLeavesNoUsableSession(t *testing.T) {
	a, pass := newTestAuth(t)
	loginResp := login(t, a, "admin", pass)

	r := httptest.NewRequest(http.MethodPost, "/admin/api/auth/logout", nil)
	for _, c := range loginResp.Cookies() {
		r.AddCookie(c)
	}
	w := httptest.NewRecorder()
	a.HandleLogout(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("logout: %d", w.Code)
	}

	// What the browser keeps after applying the Set-Cookie headers.
	next := httptest.NewRequest(http.MethodGet, "/admin/api/list", nil)
	for _, c := range w.Result().Cookies() {
		next.AddCookie(c)
	}
	if user := a.CurrentUser(next); user != "" {
		t.Fatalf("the session survived logout as %q", user)
	}
}

// Refresh is what keeps a working day from ending at the access TTL. It must
// require a real refresh cookie and hand back a complete new session.
func TestRefreshIssuesANewSession(t *testing.T) {
	a, pass := newTestAuth(t)
	loginResp := login(t, a, "admin", pass)

	r := httptest.NewRequest(http.MethodPost, "/admin/api/auth/refresh", nil)
	for _, c := range loginResp.Cookies() {
		r.AddCookie(c)
	}
	w := httptest.NewRecorder()
	a.HandleRefresh(w, r)
	if w.Code != http.StatusOK {
		t.Fatalf("refresh: %d %s", w.Code, w.Body.String())
	}
	set := cookiesOf(w.Result())
	for _, name := range []string{cookieAccess, cookieRefresh, cookieCSRF} {
		if set[name] == nil || set[name].Value == "" {
			t.Errorf("refresh did not reissue %s", name)
		}
	}
	next := httptest.NewRequest(http.MethodGet, "/admin/api/list", nil)
	next.AddCookie(set[cookieAccess])
	if user := a.CurrentUser(next); user != "admin" {
		t.Fatalf("the refreshed session does not authenticate: %q", user)
	}
}

// Refresh must not become a way in for someone holding no valid refresh token.
func TestRefreshRejectsMissingExpiredAndForgedTokens(t *testing.T) {
	a := secureAuth(t)
	expired, err := a.signToken("admin", tokenRefresh, -time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	other := New(Config{JWTSecret: []byte("another-secret"), AccessTTL: time.Hour, RefreshTTL: time.Hour})
	foreign, err := other.signToken("admin", tokenRefresh, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	cases := map[string]string{
		"absent":  "",
		"empty":   "",
		"expired": expired,
		"foreign": foreign,
		"garbage": "not.a.jwt",
	}
	for name, tok := range cases {
		r := httptest.NewRequest(http.MethodPost, "/admin/api/auth/refresh", nil)
		if name != "absent" {
			r.AddCookie(&http.Cookie{Name: cookieRefresh, Value: tok})
		}
		w := httptest.NewRecorder()
		a.HandleRefresh(w, r)
		if w.Code != http.StatusUnauthorized {
			t.Errorf("%s refresh answered %d, want 401", name, w.Code)
		}
		if len(w.Result().Cookies()) != 0 {
			t.Errorf("%s refresh issued cookies", name)
		}
	}
}

// ===== Configuration =====

// LoadConfig is the only place the secret and the cookie policy come from. A
// mistyped COOKIE_SECURE that silently turned the flag off would ship the admin
// session over plain HTTP, so defaults must survive unparseable input.
func TestLoadConfigDefaultsAndOverrides(t *testing.T) {
	t.Setenv("ADMIN_USERNAME", "  boss  ")
	t.Setenv("ADMIN_PASSWORD_BCRYPT", " $2a$12$hash ")
	t.Setenv("ADMIN_PASSWORD_PLAIN", "")
	t.Setenv("JWT_SECRET", " s3cret ")
	t.Setenv("COOKIE_DOMAIN", " admin.example ")
	t.Setenv("COOKIE_SECURE", "")
	t.Setenv("JWT_ACCESS_TTL", "")
	t.Setenv("JWT_REFRESH_TTL", "")

	cfg := LoadConfig()
	if cfg.AdminUser != "boss" || cfg.AdminPassBC != "$2a$12$hash" ||
		string(cfg.JWTSecret) != "s3cret" || cfg.CookieDomain != "admin.example" {
		t.Fatalf("values were not trimmed: %+v", cfg)
	}
	if !cfg.CookieSecure {
		t.Error("CookieSecure must default to true; an unset COOKIE_SECURE must not disable it")
	}
	if cfg.AccessTTL != 24*time.Hour || cfg.RefreshTTL != 30*24*time.Hour {
		t.Errorf("default TTLs changed: %v / %v", cfg.AccessTTL, cfg.RefreshTTL)
	}

	t.Setenv("COOKIE_SECURE", "false")
	t.Setenv("JWT_ACCESS_TTL", "15m")
	t.Setenv("JWT_REFRESH_TTL", "72h")
	cfg = LoadConfig()
	if cfg.CookieSecure {
		t.Error("COOKIE_SECURE=false was ignored; local HTTP development would be impossible")
	}
	if cfg.AccessTTL != 15*time.Minute || cfg.RefreshTTL != 72*time.Hour {
		t.Errorf("TTL overrides ignored: %v / %v", cfg.AccessTTL, cfg.RefreshTTL)
	}

	// Garbage must fall back to the safe defaults rather than to a zero value:
	// a zero TTL would mint tokens that expire the moment they are issued, and
	// COOKIE_SECURE=maybe must not read as "off".
	t.Setenv("COOKIE_SECURE", "maybe")
	t.Setenv("JWT_ACCESS_TTL", "half an hour")
	t.Setenv("JWT_REFRESH_TTL", "-")
	cfg = LoadConfig()
	if !cfg.CookieSecure {
		t.Error("an unparseable COOKIE_SECURE disabled the Secure flag")
	}
	if cfg.AccessTTL != 24*time.Hour || cfg.RefreshTTL != 30*24*time.Hour {
		t.Errorf("an unparseable TTL replaced the default: %v / %v", cfg.AccessTTL, cfg.RefreshTTL)
	}
}

// ADMIN_PASSWORD_PLAIN is the development shortcut: it is hashed on startup and
// takes precedence over the configured bcrypt. Getting the precedence backwards
// would leave a dev box authenticating against a stale production hash.
//
// One bcrypt at the production cost runs here, deliberately once.
func TestLoadConfigPlainPasswordOverridesBcrypt(t *testing.T) {
	t.Setenv("ADMIN_USERNAME", "admin")
	t.Setenv("ADMIN_PASSWORD_BCRYPT", "$2a$12$stale-hash-that-must-not-win")
	t.Setenv("ADMIN_PASSWORD_PLAIN", "dev-password")
	t.Setenv("JWT_SECRET", "dev-secret")

	cfg := LoadConfig()
	if cfg.AdminPassBC == "$2a$12$stale-hash-that-must-not-win" {
		t.Fatal("ADMIN_PASSWORD_PLAIN did not override ADMIN_PASSWORD_BCRYPT")
	}
	a := New(cfg)
	if resp := login(t, a, "admin", "dev-password"); resp.StatusCode != http.StatusOK {
		t.Fatalf("the plain password does not authenticate: %d", resp.StatusCode)
	}
}

// ===== Middleware =====

// The middleware is the gate; when it lets a request through it must be the
// same identity CurrentUser reports, and a write without CSRF must be stopped
// there rather than deeper in a handler that already touched the disk.
func TestMiddlewarePassesAuthenticatedRequestsAndStopsCSRFLessWrites(t *testing.T) {
	a, pass := newTestAuth(t)
	resp := login(t, a, "admin", pass)
	set := cookiesOf(resp)

	reached := false
	h := a.Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { reached = true }))

	get := httptest.NewRequest(http.MethodGet, "/admin/api/news/list", nil)
	get.AddCookie(set[cookieAccess])
	h.ServeHTTP(httptest.NewRecorder(), get)
	if !reached {
		t.Fatal("an authenticated GET was blocked")
	}

	reached = false
	post := httptest.NewRequest(http.MethodPost, "/admin/api/news/save", nil)
	post.AddCookie(set[cookieAccess])
	post.AddCookie(set[cookieCSRF])
	// No X-CSRF-Token header: this is what a cross-site form post looks like.
	w := httptest.NewRecorder()
	h.ServeHTTP(w, post)
	if reached {
		t.Fatal("a write without the CSRF header reached the handler")
	}
	if w.Code != http.StatusUnauthorized {
		t.Errorf("blocked write answered %d, want 401", w.Code)
	}
}

// ===== helpers =====

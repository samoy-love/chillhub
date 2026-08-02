// Package auth implements cookie/JWT authentication for the admin API:
// login/logout/refresh handlers, the nginx auth_request verifier and the
// middleware that protects every /admin route outside the allowlist.
//
// Configuration is read from the environment once, by LoadConfig, and carried
// in the Auth value instead of a package-level global, so nothing outside this
// package can reach the JWT secret.
package auth

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"errors"
	"log"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"

	"ChillHub/server/internal/adminutil"

	jwt "github.com/golang-jwt/jwt/v5"
	"golang.org/x/crypto/bcrypt"
)

// ===== Configuration =====

// Config holds everything the admin session layer needs.
type Config struct {
	AdminUser    string
	AdminPassBC  string
	JWTSecret    []byte
	CookieDomain string
	CookieSecure bool
	AccessTTL    time.Duration
	RefreshTTL   time.Duration
}

// LoadConfig reads the admin auth configuration from the environment.
func LoadConfig() Config {
	cfg := Config{
		AdminUser:    strings.TrimSpace(os.Getenv("ADMIN_USERNAME")),
		AdminPassBC:  strings.TrimSpace(os.Getenv("ADMIN_PASSWORD_BCRYPT")),
		JWTSecret:    []byte(strings.TrimSpace(os.Getenv("JWT_SECRET"))),
		CookieDomain: strings.TrimSpace(os.Getenv("COOKIE_DOMAIN")),
		CookieSecure: true,
		AccessTTL:    24 * time.Hour, // per user request: 1 day
		RefreshTTL:   30 * 24 * time.Hour,
	}
	// ADMIN_PASSWORD_PLAIN — удобство для разработки, и теперь это ПРОВЕРЯЕМЫЙ
	// факт, а не соглашение в документации.
	//
	// Раньше переменная действовала всегда и молча перекрывала
	// ADMIN_PASSWORD_BCRYPT. Ошибиться было нечем: достаточно один раз
	// скопировать dev-строку в прод-юнит — и пароль администратора лежит в
	// /etc/systemd открытым текстом, пересчитываясь bcrypt'ом при каждом
	// старте, а настоящий хеш при этом остаётся в конфиге и создаёт
	// впечатление, что используется именно он.
	//
	// Теперь ярлык включается только явным ADMIN_ALLOW_PLAIN_PASSWORD=1.
	// Прод-юнит этой строки не содержит и содержать не должен, поэтому
	// случайно перенесённый ADMIN_PASSWORD_PLAIN там ничего не включит —
	// он будет громко проигнорирован.
	if plain := strings.TrimSpace(os.Getenv("ADMIN_PASSWORD_PLAIN")); plain != "" {
		allowed, _ := strconv.ParseBool(strings.TrimSpace(os.Getenv("ADMIN_ALLOW_PLAIN_PASSWORD")))
		switch {
		case !allowed:
			// Молчать нельзя: администратор, задавший только PLAIN, иначе
			// получил бы «auth not configured» без единой подсказки почему.
			log.Print("[ADMIN AUTH] ADMIN_PASSWORD_PLAIN задан, но ADMIN_ALLOW_PLAIN_PASSWORD не выставлен — переменная ПРОИГНОРИРОВАНА. Пароль в открытом виде допустим только для разработки; на проде задавайте ADMIN_PASSWORD_BCRYPT")
		case cfg.AdminPassBC != "":
			// Две противоречащие настройки — это не «одна победит», это
			// неопределённость в том, каким паролем вообще пускают внутрь.
			log.Print("[ADMIN AUTH] заданы и ADMIN_PASSWORD_PLAIN, и ADMIN_PASSWORD_BCRYPT — задайте что-то одно. Открытый пароль ПРОИГНОРИРОВАН, действует bcrypt")
		default:
			if hb, err := bcrypt.GenerateFromPassword([]byte(plain), 12); err == nil {
				cfg.AdminPassBC = string(hb)
				// %q, and the value comes from the unit file rather than from a
				// request: nothing a client sends can reach this line.
				log.Printf("[ADMIN AUTH] режим разработки: пароль взят из ADMIN_PASSWORD_PLAIN для пользователя %q", cfg.AdminUser) // #nosec G706 -- ADMIN_USERNAME is deployment config, and %q escapes it.
			} else {
				log.Printf("[ADMIN AUTH] не удалось захешировать ADMIN_PASSWORD_PLAIN: %v", err)
			}
		}
	}
	if v := strings.TrimSpace(os.Getenv("COOKIE_SECURE")); v != "" {
		if b, err := strconv.ParseBool(v); err == nil {
			cfg.CookieSecure = b
		}
	}
	if v := strings.TrimSpace(os.Getenv("JWT_ACCESS_TTL")); v != "" {
		if d, err := time.ParseDuration(v); err == nil {
			cfg.AccessTTL = d
		}
	}
	if v := strings.TrimSpace(os.Getenv("JWT_REFRESH_TTL")); v != "" {
		if d, err := time.ParseDuration(v); err == nil {
			cfg.RefreshTTL = d
		}
	}
	return cfg
}

// Auth carries the admin auth configuration and serves the session endpoints.
type Auth struct {
	cfg Config
}

// New builds an Auth from the given configuration.
func New(cfg Config) *Auth { return &Auth{cfg: cfg} }

// ===== JWT helpers =====

type tokenType string

const (
	tokenAccess  tokenType = "access"
	tokenRefresh tokenType = "refresh"
)

type authClaims struct {
	jwt.RegisteredClaims

	Typ string `json:"typ"`
	Sub string `json:"sub"`
}

// Token rejection reasons. They are sentinels rather than one-off errors so a
// caller can tell "this deployment has no secret" (a misconfigured service that
// must be fixed) apart from "this token is not good" (a normal 401), which the
// journal cannot show once every path returns the same anonymous string.
var (
	// ErrAuthNotConfigured means JWT_SECRET is empty: nothing can be verified,
	// so nothing is authenticated.
	ErrAuthNotConfigured = errors.New("auth not configured: JWT secret is empty")
	// ErrInvalidToken means the parser accepted the shape but not the token.
	ErrInvalidToken = errors.New("invalid token")
	// ErrBadClaims means the claims were not the ones this package issues.
	ErrBadClaims = errors.New("bad claims")
	// ErrWrongTokenType means an access token was presented where a refresh one
	// was required, or the other way round.
	ErrWrongTokenType = errors.New("wrong token type")
)

func (a *Auth) signToken(sub string, typ tokenType, ttl time.Duration) (string, error) {
	now := time.Now()
	cl := authClaims{
		Typ: string(typ),
		Sub: sub,
		RegisteredClaims: jwt.RegisteredClaims{
			IssuedAt:  jwt.NewNumericDate(now),
			NotBefore: jwt.NewNumericDate(now.Add(-30 * time.Second)),
			ExpiresAt: jwt.NewNumericDate(now.Add(ttl)),
		},
	}
	t := jwt.NewWithClaims(jwt.SigningMethodHS256, cl)
	return t.SignedString(a.cfg.JWTSecret)
}

func (a *Auth) verifyToken(tokenStr string, expected tokenType) (*authClaims, error) {
	// An empty JWT_SECRET must never authenticate anyone. HS256 with an empty
	// key is a perfectly valid signature, so without this guard a service that
	// started without its systemd drop-in (first deploy, damaged override.conf,
	// manual `systemctl start`) accepts a token anybody can forge — which means
	// upload access, which means arbitrary builds shipped to every user.
	// HandleLogin already refuses in that state; this closes the other door.
	if len(a.cfg.JWTSecret) == 0 {
		return nil, ErrAuthNotConfigured
	}
	parser := jwt.NewParser(jwt.WithValidMethods([]string{jwt.SigningMethodHS256.Alg()}), jwt.WithLeeway(30*time.Second))
	tok, err := parser.ParseWithClaims(tokenStr, &authClaims{}, func(*jwt.Token) (any, error) {
		return a.cfg.JWTSecret, nil
	})
	if err != nil {
		return nil, err
	}
	if !tok.Valid {
		return nil, ErrInvalidToken
	}
	cl, ok := tok.Claims.(*authClaims)
	if !ok {
		return nil, ErrBadClaims
	}
	if cl.Typ != string(expected) {
		return nil, ErrWrongTokenType
	}
	return cl, nil
}

// ===== Cookies & CSRF =====

const (
	cookieAccess  = "access_token"
	cookieRefresh = "refresh_token"
	cookieCSRF    = "csrf_token"

	// headerCSRF — имя заголовка с CSRF-токеном в канонической записи Go
	// ("X-Csrf-Token"). Админка отправляет его как "X-CSRF-Token", и это
	// по-прежнему верно: имена заголовков в HTTP регистронезависимы, а
	// http.Header канонизирует ключ и на чтении, и на записи. Одна константа
	// вместо строкового литерала в четырёх местах — чтобы написание не
	// разъехалось при следующей правке.
	headerCSRF = "X-Csrf-Token"
)

func randCSRF() string {
	var b [32]byte
	_, _ = rand.Read(b[:])
	return base64.RawURLEncoding.EncodeToString(b[:])
}

// setCookie issues one session cookie.
//
// gosec flags the literal below because it cannot see that Secure and HttpOnly
// are set from values rather than from constants. Both are deliberate:
//   - Secure follows cfg.CookieSecure, which defaults to true and is only turned
//     off by an explicit COOKIE_SECURE=false on a plain-HTTP dev box;
//   - httpOnly is false for exactly one cookie, csrf_token, which the admin UI
//     has to read from JavaScript to echo back in the header — that is what
//     makes double-submit work.
//
// TestSessionCookiesCarryEveryProtectionFlag asserts all three flags on the
// cookies this actually issues, which is the check gosec cannot perform.
func (a *Auth) setCookie(w http.ResponseWriter, name, val string, ttl time.Duration, httpOnly bool) {
	c := &http.Cookie{ // #nosec G124 -- flags are set from config/parameter, see above.
		Name:     name,
		Value:    val,
		Path:     "/",
		Domain:   a.cfg.CookieDomain,
		Secure:   a.cfg.CookieSecure,
		HttpOnly: httpOnly,
		SameSite: http.SameSiteLaxMode,
	}
	if ttl > 0 {
		c.Expires = time.Now().Add(ttl)
		c.MaxAge = int(ttl.Seconds())
	}
	http.SetCookie(w, c)
}

// clearCookie expires one session cookie. The flags must match the ones the
// cookie was issued with, or the browser keeps the original alongside the
// expired copy — hence Secure from config again, and gosec's same blind spot.
func (a *Auth) clearCookie(w http.ResponseWriter, name string) {
	c := &http.Cookie{ // #nosec G124 -- Secure follows cfg.CookieSecure; HttpOnly and SameSite are set below.
		Name:     name,
		Value:    "",
		Path:     "/",
		Domain:   a.cfg.CookieDomain,
		Secure:   a.cfg.CookieSecure,
		HttpOnly: true,
		MaxAge:   -1,
		Expires:  time.Unix(0, 0),
		SameSite: http.SameSiteLaxMode,
	}
	http.SetCookie(w, c)
}

func (a *Auth) issueSession(w http.ResponseWriter, username string) error {
	access, err := a.signToken(username, tokenAccess, a.cfg.AccessTTL)
	if err != nil {
		return err
	}
	refresh, err := a.signToken(username, tokenRefresh, a.cfg.RefreshTTL)
	if err != nil {
		return err
	}
	csrf := randCSRF()
	a.setCookie(w, cookieAccess, access, a.cfg.AccessTTL, true)
	a.setCookie(w, cookieRefresh, refresh, a.cfg.RefreshTTL, true)
	// CSRF cookie is readable by JS (not HttpOnly)
	a.setCookie(w, cookieCSRF, csrf, a.cfg.AccessTTL, false)
	return nil
}

func (a *Auth) clearSession(w http.ResponseWriter) {
	a.clearCookie(w, cookieAccess)
	a.clearCookie(w, cookieRefresh)
	a.clearCookie(w, cookieCSRF)
}

// ===== Handlers =====

type loginRequest struct {
	Username string `json:"username"`
	Password string `json:"password"`
}

// readLoginRequest достаёт логин и пароль из запроса.
//
// Форма входа умеет оба формата: JSON шлёт admin_ui, а обычный POST формы
// остаётся рабочим запасным путём, когда JS недоступен. Ошибки разбора здесь
// намеренно не различаются — при любой из них поля остаются пустыми, и
// HandleLogin отвечает одинаковым "missing credentials". Разные ответы на
// «сломанный JSON» и «пустой пароль» рассказывали бы о форме входа больше,
// чем нужно тому, кто её перебирает.
//
// Вынесено из HandleLogin: разбор формата запроса и собственно проверка
// учётных данных — разные вещи, и держать их в одной функции значило
// пересказывать в ней оба сюжета сразу.
func readLoginRequest(r *http.Request) loginRequest {
	var in loginRequest
	if strings.Contains(r.Header.Get("Content-Type"), "application/json") {
		_ = json.NewDecoder(r.Body).Decode(&in)
	} else {
		_ = r.ParseForm()
		in.Username = r.FormValue("username")
		in.Password = r.FormValue("password")
	}
	in.Username = strings.TrimSpace(in.Username)
	return in
}

// HandleLogin authenticates the admin user and issues a session.
func (a *Auth) HandleLogin(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	in := readLoginRequest(r)
	if in.Username == "" || in.Password == "" {
		http.Error(w, "missing credentials", http.StatusBadRequest)
		return
	}
	if a.cfg.AdminUser == "" || a.cfg.AdminPassBC == "" || len(a.cfg.JWTSecret) == 0 {
		http.Error(w, "auth not configured", http.StatusInternalServerError)
		return
	}
	if !equalFoldConstantTime(in.Username, a.cfg.AdminUser) {
		http.Error(w, "invalid credentials", http.StatusUnauthorized)
		return
	}
	if bcrypt.CompareHashAndPassword([]byte(a.cfg.AdminPassBC), []byte(in.Password)) != nil {
		http.Error(w, "invalid credentials", http.StatusUnauthorized)
		return
	}
	if err := a.issueSession(w, a.cfg.AdminUser); err != nil {
		http.Error(w, "issue session failed", http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]any{"status": "ok"})
}

// HandleLogout drops the session cookies.
func (a *Auth) HandleLogout(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	a.clearSession(w)
	adminutil.WriteJSON(w, map[string]any{"status": "ok"})
}

// HandleRefresh exchanges a valid refresh cookie for a fresh session.
func (a *Auth) HandleRefresh(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	c, err := r.Cookie(cookieRefresh)
	if err != nil || c == nil || c.Value == "" {
		http.Error(w, "no refresh", http.StatusUnauthorized)
		return
	}
	cl, err := a.verifyToken(c.Value, tokenRefresh)
	if err != nil || cl == nil {
		http.Error(w, "invalid refresh", http.StatusUnauthorized)
		return
	}
	if err := a.issueSession(w, cl.Sub); err != nil {
		http.Error(w, "issue session failed", http.StatusInternalServerError)
		return
	}
	adminutil.WriteJSON(w, map[string]any{"status": "ok"})
}

// HandleMe reports the currently authenticated user.
func (a *Auth) HandleMe(w http.ResponseWriter, r *http.Request) {
	user := a.CurrentUser(r)
	if user == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	adminutil.WriteJSON(w, map[string]any{"user": user})
}

// HandleVerify is used by nginx auth_request.
func (a *Auth) HandleVerify(w http.ResponseWriter, r *http.Request) {
	if a.CurrentUser(r) == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	w.WriteHeader(http.StatusOK)
}

// CurrentUser returns the authenticated username, or "" when the request has no
// valid access cookie (or fails the CSRF check on a state-changing method).
func (a *Auth) CurrentUser(r *http.Request) string {
	c, err := r.Cookie(cookieAccess)
	if err != nil || c == nil || c.Value == "" {
		return ""
	}
	cl, err := a.verifyToken(c.Value, tokenAccess)
	if err != nil {
		return ""
	}
	if isStateChanging(r.Method) && !csrfOK(r) {
		return ""
	}
	return cl.Sub
}

// isStateChanging reports whether the method needs the CSRF check. Every
// mutating verb is listed: leaving one out makes the corresponding endpoints
// reachable from any page the admin has open.
func isStateChanging(method string) bool {
	switch method {
	case http.MethodPost, http.MethodPut, http.MethodPatch, http.MethodDelete:
		return true
	default:
		return false
	}
}

// csrfOK verifies the double-submit pair: the value the admin UI read from the
// non-HttpOnly cookie must come back in the header. A cross-site page can make
// the browser send the cookie but cannot read it, so it cannot set the header.
func csrfOK(r *http.Request) bool {
	csrfC, _ := r.Cookie(cookieCSRF)
	// Заголовок в канонической записи Go. На проводе он остаётся прежним:
	// админка шлёт "X-CSRF-Token" (см. admin_ui/admin.js), имена заголовков
	// в HTTP регистронезависимы, а Header.Get приводит ключ к канону сам.
	// Менять здесь нечего, кроме написания строки, — и клиент об этом не знает.
	csrfH := r.Header.Get(headerCSRF)
	// Both halves must be present and non-empty: an empty-vs-empty comparison
	// would otherwise "match" and let any cross-site POST through.
	if csrfC == nil || csrfC.Value == "" || csrfH == "" {
		return false
	}
	// Constant time: a plain != returns as soon as two bytes differ, and the
	// attacker controls the header, so the timing tells them how much of the
	// token they have guessed right.
	return subtle.ConstantTimeCompare([]byte(csrfH), []byte(csrfC.Value)) == 1
}

// Middleware protects /admin and /admin/api except for the allowlist below.
// The allowlist is what nginx relies on: static UI assets, the auth endpoints,
// the health probe, the admin entry point (which serves the login page itself)
// and the public feedback submit endpoint.
func (a *Auth) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		p := r.URL.Path
		// Allowlist: static UI and auth endpoints and health
		if strings.HasPrefix(p, "/admin/ui/") ||
			strings.HasPrefix(p, "/admin/api/auth/") ||
			p == "/admin/api/health" ||
			p == "/admin" || p == "/admin/" ||
			p == "/feedback/submit" {
			next.ServeHTTP(w, r)
			return
		}
		// Protect admin API routes
		if strings.HasPrefix(p, "/admin/") {
			if a.CurrentUser(r) == "" {
				http.Error(w, "unauthorized", http.StatusUnauthorized)
				return
			}
		}
		next.ServeHTTP(w, r)
	})
}

// equalFoldConstantTime сравнивает логины без учёта регистра и окружающих
// пробелов, но за время, не зависящее от того, где именно строки разошлись.
//
// Раньше здесь была пара `subtleLower(a) != subtleLower(b)`: имя обещало
// constant-time, а сравнение выполнял обычный `!=`, который останавливается на
// первом несовпавшем байте. Утечки это не давало — логин администратора не
// секрет, — но название врало, и следующий, кто скопирует эту строку для
// сравнения чего-то настоящего, унаследует не то, что прочитал.
//
// Длина через ConstantTimeEq не скрывается специально: она и так видна по
// размеру запроса.
func equalFoldConstantTime(a, b string) bool {
	x, y := strings.ToLower(strings.TrimSpace(a)), strings.ToLower(strings.TrimSpace(b))
	if len(x) != len(y) {
		return false
	}
	return subtle.ConstantTimeCompare([]byte(x), []byte(y)) == 1
}

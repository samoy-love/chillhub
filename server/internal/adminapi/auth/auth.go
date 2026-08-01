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
	// Dev-friendly precedence: if ADMIN_PASSWORD_PLAIN is provided, hash it and override any bcrypt.
	if plain := strings.TrimSpace(os.Getenv("ADMIN_PASSWORD_PLAIN")); plain != "" {
		if hb, err := bcrypt.GenerateFromPassword([]byte(plain), 12); err == nil {
			cfg.AdminPassBC = string(hb)
			log.Printf("[ADMIN AUTH] ADMIN_PASSWORD_PLAIN provided; using its bcrypt for user %q", cfg.AdminUser)
		} else {
			log.Printf("[ADMIN AUTH] Failed to hash ADMIN_PASSWORD_PLAIN: %v", err)
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
	Typ string `json:"typ"`
	Sub string `json:"sub"`
	jwt.RegisteredClaims
}

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
		return nil, errors.New("auth not configured: JWT secret is empty")
	}
	parser := jwt.NewParser(jwt.WithValidMethods([]string{jwt.SigningMethodHS256.Alg()}), jwt.WithLeeway(30*time.Second))
	tok, err := parser.ParseWithClaims(tokenStr, &authClaims{}, func(t *jwt.Token) (interface{}, error) {
		return a.cfg.JWTSecret, nil
	})
	if err != nil {
		return nil, err
	}
	if !tok.Valid {
		return nil, errors.New("invalid token")
	}
	cl, ok := tok.Claims.(*authClaims)
	if !ok {
		return nil, errors.New("bad claims")
	}
	if cl.Typ != string(expected) {
		return nil, errors.New("wrong token type")
	}
	return cl, nil
}

// ===== Cookies & CSRF =====

const (
	cookieAccess  = "access_token"
	cookieRefresh = "refresh_token"
	cookieCSRF    = "csrf_token"
)

func randCSRF() string {
	var b [32]byte
	_, _ = rand.Read(b[:])
	return base64.RawURLEncoding.EncodeToString(b[:])
}

func (a *Auth) setCookie(w http.ResponseWriter, name, val string, ttl time.Duration, httpOnly bool) {
	c := &http.Cookie{
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

func (a *Auth) clearCookie(w http.ResponseWriter, name string) {
	c := &http.Cookie{
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

// HandleLogin authenticates the admin user and issues a session.
func (a *Auth) HandleLogin(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	var in loginRequest
	ct := r.Header.Get("Content-Type")
	if strings.Contains(ct, "application/json") {
		_ = json.NewDecoder(r.Body).Decode(&in)
	} else {
		_ = r.ParseForm()
		in.Username = r.FormValue("username")
		in.Password = r.FormValue("password")
	}
	in.Username = strings.TrimSpace(in.Username)
	if in.Username == "" || in.Password == "" {
		http.Error(w, "missing credentials", http.StatusBadRequest)
		return
	}
	if a.cfg.AdminUser == "" || a.cfg.AdminPassBC == "" || len(a.cfg.JWTSecret) == 0 {
		http.Error(w, "auth not configured", http.StatusInternalServerError)
		return
	}
	if subtleLower(in.Username) != subtleLower(a.cfg.AdminUser) {
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
	// Optional: CSRF check for state-changing methods
	if r.Method == http.MethodPost || r.Method == http.MethodPut || r.Method == http.MethodPatch || r.Method == http.MethodDelete {
		csrfC, _ := r.Cookie(cookieCSRF)
		csrfH := r.Header.Get("X-CSRF-Token")
		if csrfC == nil || csrfC.Value == "" || csrfH == "" || csrfH != csrfC.Value {
			return ""
		}
	}
	return cl.Sub
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

func subtleLower(s string) string { return strings.ToLower(strings.TrimSpace(s)) }

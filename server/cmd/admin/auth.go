package main

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

	jwt "github.com/golang-jwt/jwt/v5"
	"golang.org/x/crypto/bcrypt"
)

// ===== Configuration =====

type authConfig struct {
	AdminUser    string
	AdminPassBC  string
	JWTSecret    []byte
	CookieDomain string
	CookieSecure bool
	AccessTTL    time.Duration
	RefreshTTL   time.Duration
}

var cfg authConfig

func init() {
	cfg = authConfig{
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
}

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

func signToken(sub string, typ tokenType, ttl time.Duration) (string, error) {
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
	return t.SignedString(cfg.JWTSecret)
}

func verifyToken(tokenStr string, expected tokenType) (*authClaims, error) {
	parser := jwt.NewParser(jwt.WithValidMethods([]string{jwt.SigningMethodHS256.Alg()}), jwt.WithLeeway(30*time.Second))
	tok, err := parser.ParseWithClaims(tokenStr, &authClaims{}, func(t *jwt.Token) (interface{}, error) {
		return cfg.JWTSecret, nil
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

func setCookie(w http.ResponseWriter, name, val string, ttl time.Duration, httpOnly bool) {
	c := &http.Cookie{
		Name:     name,
		Value:    val,
		Path:     "/",
		Domain:   cfg.CookieDomain,
		Secure:   cfg.CookieSecure,
		HttpOnly: httpOnly,
		SameSite: http.SameSiteLaxMode,
	}
	if ttl > 0 {
		c.Expires = time.Now().Add(ttl)
		c.MaxAge = int(ttl.Seconds())
	}
	http.SetCookie(w, c)
}

func clearCookie(w http.ResponseWriter, name string) {
	c := &http.Cookie{
		Name:     name,
		Value:    "",
		Path:     "/",
		Domain:   cfg.CookieDomain,
		Secure:   cfg.CookieSecure,
		HttpOnly: true,
		MaxAge:   -1,
		Expires:  time.Unix(0, 0),
		SameSite: http.SameSiteLaxMode,
	}
	http.SetCookie(w, c)
}

func issueSession(w http.ResponseWriter, username string) error {
	access, err := signToken(username, tokenAccess, cfg.AccessTTL)
	if err != nil {
		return err
	}
	refresh, err := signToken(username, tokenRefresh, cfg.RefreshTTL)
	if err != nil {
		return err
	}
	csrf := randCSRF()
	setCookie(w, cookieAccess, access, cfg.AccessTTL, true)
	setCookie(w, cookieRefresh, refresh, cfg.RefreshTTL, true)
	// CSRF cookie is readable by JS (not HttpOnly)
	setCookie(w, cookieCSRF, csrf, cfg.AccessTTL, false)
	return nil
}

func clearSession(w http.ResponseWriter) {
	clearCookie(w, cookieAccess)
	clearCookie(w, cookieRefresh)
	clearCookie(w, cookieCSRF)
}

// ===== Handlers =====

type loginRequest struct {
	Username string `json:"username"`
	Password string `json:"password"`
}

func handleAuthLogin(w http.ResponseWriter, r *http.Request) {
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
	if cfg.AdminUser == "" || cfg.AdminPassBC == "" || len(cfg.JWTSecret) == 0 {
		http.Error(w, "auth not configured", http.StatusInternalServerError)
		return
	}
	if subtleLower(in.Username) != subtleLower(cfg.AdminUser) {
		http.Error(w, "invalid credentials", http.StatusUnauthorized)
		return
	}
	if bcrypt.CompareHashAndPassword([]byte(cfg.AdminPassBC), []byte(in.Password)) != nil {
		http.Error(w, "invalid credentials", http.StatusUnauthorized)
		return
	}
	if err := issueSession(w, cfg.AdminUser); err != nil {
		http.Error(w, "issue session failed", http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]any{"status": "ok"})
}

func handleAuthLogout(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	clearSession(w)
	writeJSON(w, map[string]any{"status": "ok"})
}

func handleAuthRefresh(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	c, err := r.Cookie(cookieRefresh)
	if err != nil || c == nil || c.Value == "" {
		http.Error(w, "no refresh", http.StatusUnauthorized)
		return
	}
	cl, err := verifyToken(c.Value, tokenRefresh)
	if err != nil || cl == nil {
		http.Error(w, "invalid refresh", http.StatusUnauthorized)
		return
	}
	if err := issueSession(w, cl.Sub); err != nil {
		http.Error(w, "issue session failed", http.StatusInternalServerError)
		return
	}
	writeJSON(w, map[string]any{"status": "ok"})
}

func handleAuthMe(w http.ResponseWriter, r *http.Request) {
	_, user := currentUser(r)
	if user == "" {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	writeJSON(w, map[string]any{"user": user})
}

// Used by nginx auth_request
func handleAuthVerify(w http.ResponseWriter, r *http.Request) {
	if u, _ := currentUser(r); u == nil {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	w.WriteHeader(http.StatusOK)
}

// Returns (claims, username) if access ok
func currentUser(r *http.Request) (*authClaims, string) {
	c, err := r.Cookie(cookieAccess)
	if err != nil || c == nil || c.Value == "" {
		return nil, ""
	}
	cl, err := verifyToken(c.Value, tokenAccess)
	if err != nil {
		return nil, ""
	}
	// Optional: CSRF check for state-changing methods
	if r.Method == http.MethodPost || r.Method == http.MethodPut || r.Method == http.MethodPatch || r.Method == http.MethodDelete {
		csrfC, _ := r.Cookie(cookieCSRF)
		csrfH := r.Header.Get("X-CSRF-Token")
		if csrfC == nil || csrfC.Value == "" || csrfH == "" || csrfH != csrfC.Value {
			return nil, ""
		}
	}
	return cl, cl.Sub
}

// Global middleware that protects /admin and /admin/api except allowlist
func adminAuthMiddleware(next http.Handler) http.Handler {
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
			if _, user := currentUser(r); user == "" {
				http.Error(w, "unauthorized", http.StatusUnauthorized)
				return
			}
		}
		next.ServeHTTP(w, r)
	})
}

func subtleLower(s string) string { return strings.ToLower(strings.TrimSpace(s)) }

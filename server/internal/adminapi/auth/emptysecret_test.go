package auth

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	jwt "github.com/golang-jwt/jwt/v5"
)

// A service started without its systemd drop-in has an empty JWT secret.
// HS256 over an empty key is a valid signature, so without an explicit guard
// anybody could mint an admin token and reach the upload endpoints — i.e. ship
// an arbitrary build to every user. HandleLogin refuses in that state; this
// pins the cookie path shut as well.
func TestEmptySecretRejectsForgedToken(t *testing.T) {
	a := New(Config{
		JWTSecret:  []byte(""),
		AdminUser:  "admin",
		AccessTTL:  time.Hour,
		RefreshTTL: time.Hour,
	})

	forged := jwt.NewWithClaims(jwt.SigningMethodHS256, authClaims{
		Typ: string(tokenAccess),
		Sub: "root",
		RegisteredClaims: jwt.RegisteredClaims{
			IssuedAt:  jwt.NewNumericDate(time.Now()),
			ExpiresAt: jwt.NewNumericDate(time.Now().Add(time.Hour)),
		},
	})
	signed, err := forged.SignedString([]byte(""))
	if err != nil {
		t.Fatalf("sign with empty key: %v", err)
	}

	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://example.com/admin/api/list", nil)
	addTestCookie(r, cookieAccess, signed)

	if user := a.CurrentUser(r); user != "" {
		t.Fatalf("forged token accepted with empty secret: user=%q", user)
	}
}

// With a real secret the same construction must still work, otherwise the
// guard above would have broken normal authentication.
func TestRealSecretStillAuthenticates(t *testing.T) {
	a := New(Config{
		JWTSecret:  []byte("a-real-secret-value"),
		AdminUser:  "admin",
		AccessTTL:  time.Hour,
		RefreshTTL: time.Hour,
	})

	w := httptest.NewRecorder()
	if err := a.issueSession(w, "admin"); err != nil {
		t.Fatalf("issueSession: %v", err)
	}

	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://example.com/admin/api/list", nil)
	for _, c := range w.Result().Cookies() {
		r.AddCookie(c)
	}
	if user := a.CurrentUser(r); user != "admin" {
		t.Fatalf("expected admin, got %q", user)
	}
}

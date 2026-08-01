package auth

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func testAuth(t *testing.T) *Auth {
	t.Helper()
	return New(Config{
		AdminUser:  "admin",
		JWTSecret:  []byte("test-secret-value"),
		AccessTTL:  time.Hour,
		RefreshTTL: time.Hour,
	})
}

// A state-changing request needs the CSRF header to match the cookie. The
// comparison must be constant time (subtle.ConstantTimeCompare), but from the
// outside what matters is that only an exact match authenticates.
func TestCSRFHeaderMustMatchCookie(t *testing.T) {
	a := testAuth(t)
	access, err := a.signToken("admin", tokenAccess, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	const token = "abcdefghijklmnopqrstuvwxyz012345"
	cases := []struct {
		name   string
		header string
		want   string
	}{
		{"exact match", token, "admin"},
		{"missing header", "", ""},
		{"wrong first byte", "Xbcdefghijklmnopqrstuvwxyz012345", ""},
		{"wrong last byte", "abcdefghijklmnopqrstuvwxyz01234X", ""},
		{"prefix only", "abcdefghij", ""},
		{"extra suffix", token + "extra", ""},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			r := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/x", nil)
			r.AddCookie(&http.Cookie{Name: cookieAccess, Value: access})
			r.AddCookie(&http.Cookie{Name: cookieCSRF, Value: token})
			if c.header != "" {
				r.Header.Set("X-CSRF-Token", c.header)
			}
			if got := a.CurrentUser(r); got != c.want {
				t.Fatalf("CurrentUser = %q, want %q", got, c.want)
			}
		})
	}
}

// A read-only request is not subject to the CSRF check.
func TestCSRFNotRequiredForGet(t *testing.T) {
	a := testAuth(t)
	access, err := a.signToken("admin", tokenAccess, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	r := httptest.NewRequest(http.MethodGet, "http://example.com/admin/api/x", nil)
	r.AddCookie(&http.Cookie{Name: cookieAccess, Value: access})
	if got := a.CurrentUser(r); got != "admin" {
		t.Fatalf("CurrentUser = %q, want admin", got)
	}
}

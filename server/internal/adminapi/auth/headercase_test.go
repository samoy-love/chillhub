package auth

import (
	"bufio"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// The CSRF header constant is spelled in Go's canonical form, "X-Csrf-Token",
// while admin_ui/admin.js sends "X-CSRF-Token". That is only safe because HTTP
// header names are case-insensitive and net/http canonicalises them on both
// sides — and "only safe because" is exactly the kind of claim that deserves a
// test rather than a comment.
//
// The request is written out as WIRE BYTES, in the casing the admin panel
// actually sends, and handed to http.ReadRequest — the very parser the server
// runs on a connection, so the canonicalisation under test is the real one and
// nothing on the client side can normalise the mismatch away. It used to reach
// that parser through an actual TCP socket, which added nothing to the claim
// and cost a flake: the server closes the connection first, and on Windows the
// reset arrived before the answer could be read ("wsarecv: an existing
// connection was forcibly closed"), failing two runs in three.
func TestCSRFHeaderIsAcceptedInTheCasingTheAdminUISends(t *testing.T) {
	a := secureAuth(t)
	access, err := a.signToken("admin", tokenAccess, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	const csrf = "abcdefghijklmnopqrstuvwxyz012345"

	handler := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if user := a.CurrentUser(r); user != "admin" {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		w.WriteHeader(http.StatusOK)
	})

	// Every spelling a browser or a proxy might put on the wire.
	for _, spelling := range []string{"X-CSRF-Token", "X-Csrf-Token", "x-csrf-token", "X-CSRF-TOKEN"} {
		t.Run(spelling, func(t *testing.T) {
			wire := "POST /admin/api/news/save HTTP/1.1\r\n" +
				"Host: admin.example.com\r\n" +
				"Cookie: " + cookieAccess + "=" + access + "; " + cookieCSRF + "=" + csrf + "\r\n" +
				spelling + ": " + csrf + "\r\n" +
				"Content-Length: 0\r\n\r\n"
			req, err := http.ReadRequest(bufio.NewReader(strings.NewReader(wire)))
			if err != nil {
				t.Fatalf("the wire bytes did not parse as a request: %v", err)
			}
			req = req.WithContext(t.Context())

			w := httptest.NewRecorder()
			handler.ServeHTTP(w, req)

			if w.Code != http.StatusOK {
				t.Fatalf("%s was not accepted: %d %s", spelling, w.Code, strings.TrimSpace(w.Body.String()))
			}
		})
	}
}

// The constant itself must stay in the canonical form Header.Get expects. A
// non-canonical key silently reads as absent for anything that bypasses Get.
func TestCSRFHeaderConstantIsCanonical(t *testing.T) {
	if got := http.CanonicalHeaderKey(headerCSRF); got != headerCSRF {
		t.Fatalf("headerCSRF = %q, canonical form is %q", headerCSRF, got)
	}
}

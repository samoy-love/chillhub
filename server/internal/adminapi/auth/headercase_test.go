package auth

import (
	"net"
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
// This one goes through a real socket: the request line and headers are written
// by hand, in the casing the admin panel actually puts on the wire, so nothing
// in net/http's client-side normalisation can hide a mismatch. If a future
// rename breaks the contract, every write from the admin panel starts answering
// 401 and this fails first.
func TestCSRFHeaderIsAcceptedInTheCasingTheAdminUISends(t *testing.T) {
	a := secureAuth(t)
	access, err := a.signToken("admin", tokenAccess, time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	const csrf = "abcdefghijklmnopqrstuvwxyz012345"

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if user := a.CurrentUser(r); user != "admin" {
			http.Error(w, "unauthorized", http.StatusUnauthorized)
			return
		}
		w.WriteHeader(http.StatusOK)
	}))
	defer srv.Close()

	// Every spelling a browser or a proxy might put on the wire.
	for _, spelling := range []string{"X-CSRF-Token", "X-Csrf-Token", "x-csrf-token", "X-CSRF-TOKEN"} {
		t.Run(spelling, func(t *testing.T) {
			host := strings.TrimPrefix(srv.URL, "http://")
			conn, err := net.Dial("tcp", host)
			if err != nil {
				t.Fatal(err)
			}
			defer func() { _ = conn.Close() }()

			req := "POST /admin/api/news/save HTTP/1.1\r\n" +
				"Host: " + host + "\r\n" +
				"Cookie: " + cookieAccess + "=" + access + "; " + cookieCSRF + "=" + csrf + "\r\n" +
				spelling + ": " + csrf + "\r\n" +
				"Content-Length: 0\r\n" +
				"Connection: close\r\n\r\n"
			if err := conn.SetDeadline(time.Now().Add(5 * time.Second)); err != nil {
				t.Fatal(err)
			}
			if _, err := conn.Write([]byte(req)); err != nil {
				t.Fatal(err)
			}
			buf := make([]byte, 128)
			n, err := conn.Read(buf)
			if err != nil && n == 0 {
				t.Fatalf("no answer: %v", err)
			}
			if status := string(buf[:n]); !strings.HasPrefix(status, "HTTP/1.1 200") {
				t.Fatalf("%s was not accepted: %q", spelling, strings.SplitN(status, "\r\n", 2)[0])
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

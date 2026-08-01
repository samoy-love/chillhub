package httpx

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// The request id is attacker-controlled and lands in every log line, so an
// over-long or non-printable value must be replaced with a generated one.
func TestRequestIDRejectsHostileValues(t *testing.T) {
	bad := []string{
		strings.Repeat("a", maxRequestIDLen+1),
		"abc\ndef",                 // forged log line
		"abc\x1b[31m",              // terminal escape
		"id with spaces",           // unquoted separator in log lines
		strings.Repeat("x", 1<<16), // log flooding
		`"quoted"`,                 // breaks quoted log fields
	}
	for _, v := range bad {
		var seen string
		h := RequestID()(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			seen = r.Header.Get("X-Request-Id")
		}))
		req := httptest.NewRequest(http.MethodGet, "http://example.com/", nil)
		req.Header.Set("X-Request-Id", v)
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		if seen == v {
			t.Errorf("hostile request id was kept: %q", v)
		}
		if seen == "" || len(seen) > maxRequestIDLen {
			t.Errorf("no sane replacement generated for %q: %q", v, seen)
		}
		if rec.Header().Get("X-Request-Id") != seen {
			t.Errorf("echoed header %q differs from the logged id %q", rec.Header().Get("X-Request-Id"), seen)
		}
	}
}

// A well-formed id from the proxy is still preserved: correlation across nginx
// and the two Go services depends on it.
func TestRequestIDKeepsSaneValues(t *testing.T) {
	const want = "0123456789abcdef-req.1:2"
	var seen string
	h := RequestID()(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		seen = r.Header.Get("X-Request-Id")
	}))
	req := httptest.NewRequest(http.MethodGet, "http://example.com/", nil)
	req.Header.Set("X-Request-Id", want)
	h.ServeHTTP(httptest.NewRecorder(), req)
	if seen != want {
		t.Fatalf("request id = %q, want %q", seen, want)
	}
}

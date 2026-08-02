package ratelimit

import (
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func TestAllowEnforcesWindowBudget(t *testing.T) {
	l := New(3, time.Minute)
	for i := range 3 {
		if !l.Allow("1.2.3.4") {
			t.Fatalf("request %d should be allowed", i)
		}
	}
	if l.Allow("1.2.3.4") {
		t.Fatal("4th request should be rejected")
	}
	// a different client has its own budget
	if !l.Allow("5.6.7.8") {
		t.Fatal("other client should be allowed")
	}
}

func TestAllowResetsAfterWindow(t *testing.T) {
	l := New(1, 10*time.Millisecond)
	if !l.Allow("1.2.3.4") {
		t.Fatal("first request should be allowed")
	}
	if l.Allow("1.2.3.4") {
		t.Fatal("second request in the window should be rejected")
	}
	time.Sleep(20 * time.Millisecond)
	if !l.Allow("1.2.3.4") {
		t.Fatal("request after the window should be allowed")
	}
}

func TestZeroLimitDisablesLimiting(t *testing.T) {
	l := New(0, time.Minute)
	for range 100 {
		if !l.Allow("1.2.3.4") {
			t.Fatal("limiting must be off when limit <= 0")
		}
	}
	if l.Len() != 0 {
		t.Fatalf("disabled limiter must not track clients, got %d", l.Len())
	}
}

// A flood of one-off addresses must not grow the map without bound.
func TestSweepDropsExpiredEntries(t *testing.T) {
	l := New(10, time.Millisecond)
	for i := range 2000 {
		l.Allow(fmt.Sprintf("10.0.%d.%d", i/256, i%256))
	}
	// every window above is long expired by now; one more sweep-triggering pass
	time.Sleep(20 * time.Millisecond)
	before := l.Len()
	for range gcEvery {
		l.Allow("192.168.0.1")
	}
	if l.Len() >= before {
		t.Fatalf("sweep did not reclaim entries: %d -> %d", before, l.Len())
	}
}

// The age-based sweep alone cannot help when every address is fresh: that is
// exactly the flood the cap exists for, and the map used to grow without bound.
func TestSweepEnforcesHardCapWhenAllEntriesAreFresh(t *testing.T) {
	l := New(10, time.Hour) // nothing can expire during the test
	for i := range gcMaxEntries + 2000 {
		l.Allow(fmt.Sprintf("10.%d.%d.%d", i/65536, (i/256)%256, i%256))
	}
	if n := l.Len(); n > gcMaxEntries {
		t.Fatalf("tracked %d addresses, hard cap is %d", n, gcMaxEntries)
	}
	// Eviction must not disable limiting for an address that is still tracked.
	for range 10 {
		l.Allow("203.0.113.99")
	}
	if l.Allow("203.0.113.99") {
		t.Fatal("budget must still be enforced after an eviction pass")
	}
}

func TestMiddlewareRejectsOverBudget(t *testing.T) {
	l := New(2, time.Minute)
	h := l.Middleware(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))
	codes := make([]int, 0, 3)
	for range 3 {
		req := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://example.com/api/games", nil)
		req.RemoteAddr = "203.0.113.7:5555"
		w := httptest.NewRecorder()
		h.ServeHTTP(w, req)
		codes = append(codes, w.Code)
	}
	if codes[0] != 200 || codes[1] != 200 || codes[2] != http.StatusTooManyRequests {
		t.Fatalf("unexpected status sequence: %v", codes)
	}
}

// OPTIONS is exempt so that CORS preflight never burns budget.
func TestMiddlewareSkipsOptions(t *testing.T) {
	l := New(1, time.Minute)
	h := l.Middleware(http.HandlerFunc(func(_ http.ResponseWriter, _ *http.Request) {}))
	for range 10 {
		req := httptest.NewRequestWithContext(t.Context(), http.MethodOptions, "http://example.com/api/games", nil)
		req.RemoteAddr = "203.0.113.8:5555"
		w := httptest.NewRecorder()
		h.ServeHTTP(w, req)
		if w.Code == http.StatusTooManyRequests {
			t.Fatal("preflight must not be rate limited")
		}
	}
}

func TestClientIPPrefersForwardedHeaders(t *testing.T) {
	cases := []struct {
		name   string
		set    func(*http.Request)
		remote string
		want   string
	}{
		{
			// nginx uses $proxy_add_x_forwarded_for, which APPENDS the real
			// address to whatever the client sent. Only the LAST element was
			// added by our proxy; everything to the left is attacker-supplied.
			// Reading the first element let anyone reset their own counter by
			// sending a random X-Forwarded-For on every request.
			name:   "x-forwarded-for chain: trust the last hop, not the client",
			set:    func(r *http.Request) { r.Header.Set("X-Forwarded-For", "1.2.3.4, 198.51.100.5") },
			remote: "10.0.0.1:443",
			want:   "198.51.100.5",
		},
		{
			// X-Real-IP is set by nginx from $remote_addr and cannot be spoofed,
			// so it outranks the forwarded chain entirely.
			name: "x-real-ip wins over a forged forwarded chain",
			set: func(r *http.Request) {
				r.Header.Set("X-Forwarded-For", "1.2.3.4")
				r.Header.Set("X-Real-IP", "198.51.100.6")
			},
			remote: "10.0.0.1:443",
			want:   "198.51.100.6",
		},
		{
			name:   "remote addr fallback",
			set:    func(_ *http.Request) {},
			remote: "198.51.100.7:12345",
			want:   "198.51.100.7",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "http://example.com/", nil)
			req.RemoteAddr = tc.remote
			tc.set(req)
			if got := ClientIP(req); got != tc.want {
				t.Fatalf("ClientIP = %q, want %q", got, tc.want)
			}
		})
	}
}

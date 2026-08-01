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
	for i := 0; i < 3; i++ {
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
	for i := 0; i < 100; i++ {
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
	for i := 0; i < 2000; i++ {
		l.Allow(fmt.Sprintf("10.0.%d.%d", i/256, i%256))
	}
	// every window above is long expired by now; one more sweep-triggering pass
	time.Sleep(20 * time.Millisecond)
	before := l.Len()
	for i := 0; i < gcEvery; i++ {
		l.Allow("192.168.0.1")
	}
	if l.Len() >= before {
		t.Fatalf("sweep did not reclaim entries: %d -> %d", before, l.Len())
	}
}

func TestMiddlewareRejectsOverBudget(t *testing.T) {
	l := New(2, time.Minute)
	h := l.Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))
	codes := make([]int, 0, 3)
	for i := 0; i < 3; i++ {
		req := httptest.NewRequest(http.MethodGet, "http://example.com/api/games", nil)
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
	h := l.Middleware(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {}))
	for i := 0; i < 10; i++ {
		req := httptest.NewRequest(http.MethodOptions, "http://example.com/api/games", nil)
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
			name:   "x-forwarded-for chain",
			set:    func(r *http.Request) { r.Header.Set("X-Forwarded-For", "198.51.100.5, 10.0.0.1") },
			remote: "10.0.0.1:443",
			want:   "198.51.100.5",
		},
		{
			name:   "x-real-ip",
			set:    func(r *http.Request) { r.Header.Set("X-Real-IP", "198.51.100.6") },
			remote: "10.0.0.1:443",
			want:   "198.51.100.6",
		},
		{
			name:   "remote addr fallback",
			set:    func(r *http.Request) {},
			remote: "198.51.100.7:12345",
			want:   "198.51.100.7",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			req := httptest.NewRequest(http.MethodGet, "http://example.com/", nil)
			req.RemoteAddr = tc.remote
			tc.set(req)
			if got := ClientIP(req); got != tc.want {
				t.Fatalf("ClientIP = %q, want %q", got, tc.want)
			}
		})
	}
}

// Package ratelimit provides a small fixed-window per-IP rate limiter shared by
// the public API and the admin feedback endpoint.
//
// The counter map is swept periodically (and force-swept once it grows past a
// hard cap), so a flood of distinct source addresses cannot leak memory.
package ratelimit

import (
	"net/http"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ClientIP returns the caller's address, honouring the headers nginx sets when
// it proxies the request. X-Forwarded-For may carry a chain; the first entry is
// the original client.
func ClientIP(r *http.Request) string {
	if xff := strings.TrimSpace(r.Header.Get("X-Forwarded-For")); xff != "" {
		if i := strings.IndexByte(xff, ','); i >= 0 {
			return strings.TrimSpace(xff[:i])
		}
		return xff
	}
	if rip := strings.TrimSpace(r.Header.Get("X-Real-IP")); rip != "" {
		return rip
	}
	host := strings.TrimSpace(r.RemoteAddr)
	if i := strings.LastIndexByte(host, ':'); i > 0 {
		host = host[:i]
	}
	return host
}

const (
	// how often (in allowed requests) stale entries are swept
	gcEvery = 128
	// hard cap on tracked IPs; a sweep is forced once exceeded
	gcMaxEntries = 10000
)

type entry struct {
	count       int
	windowStart time.Time
}

// Limiter allows at most Limit requests per Window from one client address.
// The zero value is not usable; construct it with New.
type Limiter struct {
	limit  int
	window time.Duration

	mu       sync.Mutex
	entries  map[string]entry
	reqCount int
}

// New returns a limiter allowing limit requests per window. Non-positive
// arguments disable limiting (Allow always returns true).
func New(limit int, window time.Duration) *Limiter {
	return &Limiter{limit: limit, window: window, entries: make(map[string]entry)}
}

// Allow records one request from ip and reports whether it fits in the budget.
func (l *Limiter) Allow(ip string) bool {
	if l == nil || l.limit <= 0 || l.window <= 0 {
		return true
	}
	now := time.Now()
	l.mu.Lock()
	defer l.mu.Unlock()
	// periodic GC so the map cannot grow without bound
	l.reqCount++
	if l.reqCount%gcEvery == 0 || len(l.entries) > gcMaxEntries {
		l.sweepLocked(now)
	}
	st := l.entries[ip]
	if st.windowStart.IsZero() || now.Sub(st.windowStart) > l.window {
		st = entry{count: 0, windowStart: now}
	}
	if st.count >= l.limit {
		l.entries[ip] = st
		return false
	}
	st.count++
	l.entries[ip] = st
	return true
}

// Len reports how many client addresses are currently tracked.
func (l *Limiter) Len() int {
	l.mu.Lock()
	defer l.mu.Unlock()
	return len(l.entries)
}

// sweepLocked drops entries whose window is long expired. Callers hold l.mu.
func (l *Limiter) sweepLocked(now time.Time) {
	for ip, st := range l.entries {
		if st.windowStart.IsZero() || now.Sub(st.windowStart) > 10*l.window {
			delete(l.entries, ip)
		}
	}
}

// Middleware rejects over-budget requests with 429 before they reach next.
// Methods that cannot change state and cost nothing (OPTIONS) are exempt.
func (l *Limiter) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodOptions {
			next.ServeHTTP(w, r)
			return
		}
		if !l.Allow(ClientIP(r)) {
			w.Header().Set("Retry-After", retryAfter(l.window))
			http.Error(w, "too many requests", http.StatusTooManyRequests)
			return
		}
		next.ServeHTTP(w, r)
	})
}

// Wrap is Middleware for a single http.HandlerFunc; only the given methods are
// counted (an empty list counts everything but OPTIONS).
func (l *Limiter) Wrap(h http.HandlerFunc, methods ...string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodOptions {
			h(w, r)
			return
		}
		if len(methods) > 0 {
			counted := false
			for _, m := range methods {
				if r.Method == m {
					counted = true
					break
				}
			}
			if !counted {
				h(w, r)
				return
			}
		}
		if !l.Allow(ClientIP(r)) {
			http.Error(w, "too many requests", http.StatusTooManyRequests)
			return
		}
		h(w, r)
	}
}

func retryAfter(d time.Duration) string {
	secs := int(d.Seconds())
	if secs < 1 {
		secs = 1
	}
	return strconv.Itoa(secs)
}

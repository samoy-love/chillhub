// Package ratelimit provides a small fixed-window per-IP rate limiter shared by
// the public API and the admin feedback endpoint.
//
// The counter map is swept periodically (and force-swept once it grows past a
// hard cap), so a flood of distinct source addresses cannot leak memory.
package ratelimit

import (
	"net/http"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ClientIP returns the caller's address, honouring the headers nginx sets when
// it proxies the request. X-Forwarded-For may carry a chain; the first entry is
// the original client.
func ClientIP(r *http.Request) string {
	// X-Real-IP первым: nginx выставляет его жёстко из $remote_addr, подделать
	// его клиент не может.
	//
	// X-Forwarded-For НЕЛЬЗЯ читать слева: nginx использует
	// $proxy_add_x_forwarded_for, который ДОПИСЫВАЕТ реальный адрес к тому,
	// что прислал клиент. То есть первый элемент полностью подконтролен
	// клиенту, и лимит обходится случайным заголовком на каждый запрос.
	// Доверять можно только последнему элементу — его добавил наш прокси.
	if rip := strings.TrimSpace(r.Header.Get("X-Real-IP")); rip != "" {
		return rip
	}
	if xff := strings.TrimSpace(r.Header.Get("X-Forwarded-For")); xff != "" {
		if i := strings.LastIndexByte(xff, ','); i >= 0 {
			return strings.TrimSpace(xff[i+1:])
		}
		return xff
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
	// how far below the cap an over-cap eviction trims, so that the (sorted)
	// eviction pass runs once in a while rather than on every request that
	// arrives while the map sits exactly at the cap.
	gcTargetEntries = gcMaxEntries * 3 / 4
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

// sweepLocked drops entries whose window is long expired and, if that was not
// enough, evicts the oldest ones until the map is back under the cap. Callers
// hold l.mu.
//
// The age-based pass alone did NOT keep the promise in the package doc: it only
// removes entries older than 10 windows, so a flood from freshly seen addresses
// — exactly the case the cap exists for — left every entry in place and the map
// grew without bound. Evicting the oldest entries is a real bound.
//
// Evicting an entry resets that address's counter. The oldest entries are the
// ones closest to their window expiring anyway, so the budget an attacker can
// recover this way is negligible compared to the memory a hard cap saves; and
// reaching this path at all requires more than gcMaxEntries distinct addresses
// inside one window.
func (l *Limiter) sweepLocked(now time.Time) {
	for ip, st := range l.entries {
		if st.windowStart.IsZero() || now.Sub(st.windowStart) > 10*l.window {
			delete(l.entries, ip)
		}
	}
	if len(l.entries) <= gcMaxEntries {
		return
	}
	type aged struct {
		ip    string
		start time.Time
	}
	all := make([]aged, 0, len(l.entries))
	for ip, st := range l.entries {
		all = append(all, aged{ip: ip, start: st.windowStart})
	}
	sort.Slice(all, func(i, j int) bool { return all[i].start.Before(all[j].start) })
	for i := 0; i < len(all)-gcTargetEntries; i++ {
		delete(l.entries, all[i].ip)
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

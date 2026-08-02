package httpx

import (
	"net/http"
	"strconv"
	"time"

	"ChillHub/server/internal/promexp"
)

// RouteFunc names the route a request matched. It exists because the two
// ChillHub processes route differently — the public API uses gorilla/mux with
// path templates, the admin uses net/http's ServeMux with a fixed list of
// patterns — and the middleware must not learn either.
//
// It MUST return a bounded set of values. Using r.URL.Path here would let any
// stranger create a new time series per request (/a, /aa, /aaa ...) and bloat
// the TSDB from the outside; every implementation folds unknown paths into a
// single "other".
type RouteFunc func(*http.Request) string

// httpBuckets are request-latency buckets in seconds. The upper end is 10s
// because that is already a broken request for JSON endpoints; uploads and
// content downloads are served by nginx in production and never reach here.
var httpBuckets = []float64{0.005, 0.025, 0.1, 0.5, 1, 2.5, 10}

// Metrics returns middleware that counts responses by route, method and status
// code and observes the latency of each route.
//
// Note it wraps the OUTERMOST layer in both processes, so a request rejected by
// auth or by the rate limiter is counted too: "everything got 429" is precisely
// the kind of state that must be visible on a graph rather than only in a log.
func Metrics(reg *promexp.Registry, service string, route RouteFunc) func(http.Handler) http.Handler {
	requests := reg.NewCounter("chillhub_http_requests_total",
		"Ответы HTTP по маршруту, методу и коду", "service", "route", "method", "code")
	duration := reg.NewHistogram("chillhub_http_request_duration_seconds",
		"Время ответа", httpBuckets, "service", "route")

	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			start := time.Now()
			lrw := &loggingResponseWriter{ResponseWriter: w, status: http.StatusOK}
			next.ServeHTTP(lrw, r)
			// The route is resolved AFTER the handler ran: gorilla/mux only
			// attaches the matched route to the request while serving it.
			name := "other"
			if route != nil {
				if v := route(r); v != "" {
					name = v
				}
			}
			requests.Inc(service, name, method(r.Method), strconv.Itoa(lrw.status))
			duration.Observe(time.Since(start).Seconds(), service, name)
		})
	}
}

// method folds anything outside the HTTP methods this server implements into
// one value: the method is attacker-controlled and would otherwise be a second
// way to invent time series.
func method(m string) string {
	switch m {
	case http.MethodGet, http.MethodHead, http.MethodPost, http.MethodPut,
		http.MethodPatch, http.MethodDelete, http.MethodOptions:
		return m
	}
	return "other"
}

// Observe wraps a handler and reports the status code it produced.
//
// It exists so that a business counter ("a version was activated") can be
// attached at the routing table without every handler package learning about
// metrics: the handlers keep answering HTTP, and what an answer MEANS stays in
// one place next to the route it belongs to.
func Observe(h http.HandlerFunc, fn func(status int)) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		lrw := &loggingResponseWriter{ResponseWriter: w, status: http.StatusOK}
		h(lrw, r)
		fn(lrw.status)
	}
}

// StaticRoutes builds a RouteFunc for a known set of exact patterns plus a list
// of prefixes. It is what the admin process uses: its ServeMux patterns are
// already enumerated at start-up, so the allowlist costs nothing to keep
// current.
func StaticRoutes(exact []string, prefixes []string) RouteFunc {
	set := make(map[string]struct{}, len(exact))
	for _, p := range exact {
		set[p] = struct{}{}
	}
	return func(r *http.Request) string {
		p := r.URL.Path
		if _, ok := set[p]; ok {
			return p
		}
		for _, pref := range prefixes {
			if len(p) >= len(pref) && p[:len(pref)] == pref {
				return pref
			}
		}
		return "other"
	}
}

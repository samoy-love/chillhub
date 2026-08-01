package httpx

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"log"
	"net/http"
	"strings"
	"time"
)

// loggingResponseWriter captures status code
type loggingResponseWriter struct {
	http.ResponseWriter
	status int
}

func (lrw *loggingResponseWriter) WriteHeader(statusCode int) {
	lrw.status = statusCode
	lrw.ResponseWriter.WriteHeader(statusCode)
}

// Flush forwards to the underlying ResponseWriter if it implements http.Flusher.
func (lrw *loggingResponseWriter) Flush() {
	if f, ok := lrw.ResponseWriter.(http.Flusher); ok {
		f.Flush()
	}
}

// Logging returns middleware that logs method, url, status and duration with an optional label prefix
func Logging(label string) func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			start := time.Now()
			lrw := &loggingResponseWriter{ResponseWriter: w, status: 200}
			next.ServeHTTP(lrw, r)
			dur := time.Since(start)
			reqID := r.Header.Get("X-Request-Id")
			if label != "" {
				if reqID != "" {
					log.Printf("%s %s %s %d %s reqid=%s", label, r.Method, r.URL.String(), lrw.status, dur, reqID)
				} else {
					log.Printf("%s %s %s %d %s", label, r.Method, r.URL.String(), lrw.status, dur)
				}
			} else {
				if reqID != "" {
					log.Printf("%s %s %d %s reqid=%s", r.Method, r.URL.String(), lrw.status, dur, reqID)
				} else {
					log.Printf("%s %s %d %s", r.Method, r.URL.String(), lrw.status, dur)
				}
			}
		})
	}
}

// NoStore adds headers to disable caching
func NoStore(h http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Pragma", "no-cache")
		w.Header().Set("Expires", "0")
		h.ServeHTTP(w, r)
	})
}

// RequestID middleware: ensures each request has an X-Request-Id header (incoming preserved, otherwise generated)
func RequestID() func(http.Handler) http.Handler {
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			rid := strings.TrimSpace(r.Header.Get("X-Request-Id"))
			if rid == "" {
				// generate 16 random bytes as hex (32 chars)
				var b [16]byte
				if _, err := rand.Read(b[:]); err == nil {
					rid = hex.EncodeToString(b[:])
				} else {
					rid = time.Now().UTC().Format("20060102150405.000000000")
				}
				r = r.Clone(r.Context())
				r.Header.Set("X-Request-Id", rid)
			}
			w.Header().Set("X-Request-Id", rid)
			next.ServeHTTP(w, r)
		})
	}
}

// CORSDisabled is the origin spec that turns cross-origin access off entirely.
// Preflight requests are still answered (so they never reach the handlers),
// but no Access-Control-* headers are emitted, which keeps the API same-origin.
const CORSDisabled = "none"

// CORS adds CORS headers according to the given origin spec, which may be:
//   - "*"                 allow any origin (cookies are not usable cross-site)
//   - "none" / "off"      emit no CORS headers at all (same-origin only)
//   - "a.example,b.example" comma-separated allow-list of exact origins; a
//     matching request Origin is echoed back and credentials are allowed
//
// An empty spec keeps the historical "*" behaviour.
func CORS(origin string) func(http.Handler) http.Handler {
	spec := strings.TrimSpace(origin)
	if spec == "" {
		spec = "*"
	}
	disabled := strings.EqualFold(spec, CORSDisabled) || strings.EqualFold(spec, "off")
	wildcard := spec == "*"
	var allowList []string
	if !disabled && !wildcard {
		for _, o := range strings.Split(spec, ",") {
			if o = strings.TrimSpace(o); o != "" {
				allowList = append(allowList, o)
			}
		}
	}
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			switch {
			case disabled:
				// no CORS headers; still short-circuit preflight below
			case wildcard:
				w.Header().Set("Access-Control-Allow-Origin", "*")
				w.Header().Set("Vary", "Origin")
			default:
				reqOrigin := strings.TrimSpace(r.Header.Get("Origin"))
				w.Header().Set("Vary", "Origin")
				for _, o := range allowList {
					if strings.EqualFold(o, reqOrigin) {
						w.Header().Set("Access-Control-Allow-Origin", reqOrigin)
						// exact origin echo makes cookie-authenticated calls possible
						w.Header().Set("Access-Control-Allow-Credentials", "true")
						break
					}
				}
			}
			if !disabled {
				w.Header().Set("Access-Control-Allow-Methods", "GET,POST,PUT,PATCH,DELETE,OPTIONS")
				w.Header().Set("Access-Control-Allow-Headers", "*, Authorization, Content-Type, X-Requested-With, X-Request-Id")
				w.Header().Set("Access-Control-Expose-Headers", "X-Request-Id, Content-Length")
			}
			// Preflight is answered here so that OPTIONS never reaches a mutating handler.
			if r.Method == http.MethodOptions {
				w.WriteHeader(http.StatusNoContent)
				return
			}
			next.ServeHTTP(w, r)
		})
	}
}

// WriteJSON writes value as application/json with no-store headers
func WriteJSON(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Expires", "0")
	_ = json.NewEncoder(w).Encode(v)
}

// BaseURL builds external base URL honoring X-Forwarded-* headers
func BaseURL(r *http.Request) string {
	proto := strings.TrimSpace(r.Header.Get("X-Forwarded-Proto"))
	if proto == "" {
		if r.TLS != nil {
			proto = "https"
		} else {
			proto = "http"
		}
	}
	host := strings.TrimSpace(r.Header.Get("X-Forwarded-Host"))
	if host == "" {
		host = r.Host
	}
	port := strings.TrimSpace(r.Header.Get("X-Forwarded-Port"))
	if port != "" && !strings.Contains(host, ":") {
		if !(proto == "http" && port == "80") && !(proto == "https" && port == "443") {
			host = host + ":" + port
		}
	}
	return proto + "://" + host
}

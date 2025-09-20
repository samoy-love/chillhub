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

// CORS adds permissive CORS headers. Pass origin "*" to allow any origin.
func CORS(origin string) func(http.Handler) http.Handler {
	allowOrigin := strings.TrimSpace(origin)
	if allowOrigin == "" {
		allowOrigin = "*"
	}
	return func(next http.Handler) http.Handler {
		return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			// Basic permissive headers; can be made stricter via env later
			w.Header().Set("Access-Control-Allow-Origin", allowOrigin)
			w.Header().Set("Vary", "Origin")
			w.Header().Set("Access-Control-Allow-Methods", "GET,POST,PUT,PATCH,DELETE,OPTIONS")
			w.Header().Set("Access-Control-Allow-Headers", "*, Authorization, Content-Type, X-Requested-With, X-Request-Id")
			w.Header().Set("Access-Control-Expose-Headers", "X-Request-Id, Content-Length")
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

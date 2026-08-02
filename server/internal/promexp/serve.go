package promexp

import (
	"log"
	"net"
	"net/http"
	"strings"
	"time"
)

// Path is where every ChillHub process exposes its registry.
//
// The prefix is "/internal/" and not "/metrics" for two reasons. First,
// /metrics/report on the admin process is the launcher's telemetry INGEST — two
// unrelated things must not look alike in an nginx config that is edited by
// hand. Second, no vhost proxies anything under /internal/, so if this listener
// is ever pointed at a port nginx does forward, the path itself still does not
// resolve to a public route.
const Path = "/internal/metrics"

// Serve starts an HTTP server dedicated to the metrics endpoint and blocks.
//
// It is a SEPARATE listener rather than one more route on the application
// server on purpose: the public API (:55700) and the admin API (:55777) are
// both proxied by nginx, and a route added there is one forgotten `location`
// away from being world-readable. Product metrics say how many people installed
// what and how often anything failed — a competitive and reputational picture
// that has no business on the open internet. A distinct port cannot be exposed
// by accident: it has to be added to nginx explicitly.
//
// The default address is loopback (see httpx.ListenAddr). Prometheus runs in a
// container and reaches the host over the docker bridge, so production sets the
// bridge address explicitly; anything that is not loopback is logged loudly,
// because "temporarily bound to 0.0.0.0" is exactly the kind of change that
// survives until someone scans the port.
func Serve(addr string, reg *Registry) {
	if !isLoopback(addr) {
		log.Printf("[metrics] ВНИМАНИЕ: экспортёр слушает не loopback (%s) — убедитесь, что порт закрыт снаружи", addr)
	}
	mux := http.NewServeMux()
	mux.Handle(Path, reg.Handler())
	// Anything else on this port is a mistake worth seeing as a 404 rather than
	// as a directory listing or a redirect loop.
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) { http.NotFound(w, r) })

	srv := &http.Server{
		Addr:              addr,
		Handler:           mux,
		ReadHeaderTimeout: 5 * time.Second,
		ReadTimeout:       10 * time.Second,
		WriteTimeout:      30 * time.Second,
		IdleTimeout:       60 * time.Second,
	}
	log.Printf("[metrics] экспортёр Prometheus на http://%s%s", addr, Path)
	if err := srv.ListenAndServe(); err != nil {
		// A failed exporter must not take the service with it: metrics are an
		// observation of the product, not part of it.
		log.Printf("[metrics] экспортёр остановлен: %v", err)
	}
}

func isLoopback(addr string) bool {
	host, _, err := net.SplitHostPort(strings.TrimSpace(addr))
	if err != nil {
		return false
	}
	if host == "" {
		return false // ":9101" means every interface
	}
	if host == "localhost" {
		return true
	}
	ip := net.ParseIP(host)
	return ip != nil && ip.IsLoopback()
}

package httpx

import (
	"net"
	"os"
	"strconv"
	"strings"
)

// ListenAddr resolves the TCP address a server should bind to.
//
// Both ChillHub servers run behind nginx, which proxies to 127.0.0.1, so the
// default binds to the loopback interface only: a bare ":port" would expose the
// admin API and the public API on every interface of the box, letting anyone
// reach them directly and bypass the TLS termination, the access log and the
// rate limits nginx applies. Defence in depth — the firewall closes those ports
// too, but a firewall rule is not part of the deployment artefact and the code
// must not depend on it.
//
// envVar (ADMIN_LISTEN_ADDR / API_LISTEN_ADDR) overrides it for development,
// where the launcher on another machine may need to reach the dev box. The
// value is a normal Go listen address: "host:port", ":port" (all interfaces) or
// just "port" — the latter two are accepted as a convenience.
func ListenAddr(envVar string, defaultPort int) string {
	def := "127.0.0.1:" + strconv.Itoa(defaultPort)
	v := strings.TrimSpace(os.Getenv(envVar))
	if v == "" {
		return def
	}
	// A bare port number ("55700") is a common shorthand; turn it into ":port",
	// which is what a user typing only a port means (all interfaces).
	if _, err := strconv.Atoi(v); err == nil {
		return ":" + v
	}
	if _, _, err := net.SplitHostPort(v); err != nil {
		// Not a valid host:port pair — treat it as a host and append the port.
		return net.JoinHostPort(v, strconv.Itoa(defaultPort))
	}
	return v
}

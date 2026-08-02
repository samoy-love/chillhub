package httpx

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func ok(w http.ResponseWriter, _ *http.Request) { w.WriteHeader(http.StatusOK) }

// Manifests, content and news are served with no-store on purpose: a stale manifest
// makes the launcher compare against the wrong build and either miss an update or
// download files that no longer exist.
func TestNoStoreSetsHeadersOnEveryResponse(t *testing.T) {
	h := NoStore(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusNotFound)
	}))
	w := httptest.NewRecorder()
	h.ServeHTTP(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/manifests/x.json", nil))

	cc := w.Header().Get("Cache-Control")
	if !strings.Contains(cc, "no-store") {
		// Must hold on 4xx too: an error page cached in place of a manifest is worse
		// than the error itself.
		t.Fatalf("Cache-Control = %q, want no-store even on 404", cc)
	}
}

// CORS with a concrete origin must not echo an arbitrary one: the admin API
// authenticates with cookies, so echoing back whatever asked would be a hole.
func TestCORSDoesNotEchoArbitraryOrigin(t *testing.T) {
	h := CORS("https://admin.example")(http.HandlerFunc(ok))

	w := httptest.NewRecorder()
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/list", nil)
	r.Header.Set("Origin", "https://evil.example")
	h.ServeHTTP(w, r)

	if got := w.Header().Get("Access-Control-Allow-Origin"); got == "https://evil.example" {
		t.Fatal("an arbitrary Origin was echoed back — any site could call the admin API with the user's cookies")
	}
}

// A preflight must not reach the handler: it carries no body and no session.
func TestCORSHandlesPreflightWithoutCallingHandler(t *testing.T) {
	called := false
	h := CORS("*")(http.HandlerFunc(func(_ http.ResponseWriter, _ *http.Request) { called = true }))

	w := httptest.NewRecorder()
	r := httptest.NewRequestWithContext(t.Context(), http.MethodOptions, "/api/games", nil)
	r.Header.Set("Origin", "https://example.com")
	r.Header.Set("Access-Control-Request-Method", "GET")
	h.ServeHTTP(w, r)

	if called {
		t.Error("preflight reached the handler")
	}
	if w.Code >= 400 {
		t.Errorf("preflight answered %d; browsers treat that as a denied request", w.Code)
	}
}

// CORSDisabled emits no CORS headers at all. This is what the admin service uses:
// its UI is served from the same origin, and the API authenticates with cookies,
// so any cross-origin access would be a hole rather than a feature.
func TestCORSDisabledEmitsNoHeaders(t *testing.T) {
	h := CORS(CORSDisabled)(http.HandlerFunc(ok))
	w := httptest.NewRecorder()
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/list", nil)
	r.Header.Set("Origin", "https://example.com")
	h.ServeHTTP(w, r)

	if v := w.Header().Get("Access-Control-Allow-Origin"); v != "" {
		t.Errorf("CORS answered %q while disabled", v)
	}
}

// An EMPTY spec means wildcard, not "off".
//
// No caller relies on that today — cmd/api passes "*" explicitly and cmd/admin
// returns CORSDisabled when ADMIN_CORS_ORIGIN is unset. But the mapping is a trap
// worth pinning: a future `CORS(os.Getenv("SOMETHING"))` with the variable unset
// would silently open the endpoint to every origin. If this test ever starts
// failing because empty now means "off", that is an improvement — delete it.
func TestCORSEmptySpecMeansWildcard(t *testing.T) {
	h := CORS("")(http.HandlerFunc(ok))
	w := httptest.NewRecorder()
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil)
	r.Header.Set("Origin", "https://example.com")
	h.ServeHTTP(w, r)

	if got := w.Header().Get("Access-Control-Allow-Origin"); got != "*" {
		t.Fatalf("empty spec produced %q; the documented behaviour is wildcard", got)
	}
}

// A configured origin IS echoed when it matches — that is what makes
// cookie-authenticated cross-origin calls possible for an allowlisted admin UI.
func TestCORSEchoesAllowlistedOrigin(t *testing.T) {
	h := CORS("https://admin.example")(http.HandlerFunc(ok))
	w := httptest.NewRecorder()
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/list", nil)
	r.Header.Set("Origin", "https://admin.example")
	h.ServeHTTP(w, r)

	if got := w.Header().Get("Access-Control-Allow-Origin"); got != "https://admin.example" {
		t.Fatalf("allowlisted origin was not echoed: %q", got)
	}
}

// Logging must not swallow the handler's status code or body.
func TestLoggingPassesResponseThrough(t *testing.T) {
	h := Logging("TEST")(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusTeapot)
		_, _ = w.Write([]byte("тело"))
	}))
	w := httptest.NewRecorder()
	h.ServeHTTP(w, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/x", nil))

	if w.Code != http.StatusTeapot {
		t.Errorf("status = %d, want 418", w.Code)
	}
	if w.Body.String() != "тело" {
		t.Errorf("body = %q", w.Body.String())
	}
}

// WriteJSON must set the content type: without it browsers sniff, and the admin UI
// parses the answer as JSON regardless.
func TestWriteJSONSetsContentTypeAndEncodes(t *testing.T) {
	w := httptest.NewRecorder()
	WriteJSON(w, map[string]any{"status": "ok", "n": 42})

	if ct := w.Header().Get("Content-Type"); !strings.Contains(ct, "application/json") {
		t.Errorf("Content-Type = %q", ct)
	}
	var got map[string]any
	if err := json.Unmarshal(w.Body.Bytes(), &got); err != nil {
		t.Fatalf("body is not JSON: %v", err)
	}
	if got["status"] != "ok" {
		t.Errorf("payload lost: %v", got)
	}
}

// Manifest URLs handed to clients are built from this. Ignoring the proxy headers
// nginx sets would point every launcher at http://127.0.0.1.
func TestBaseURLFollowsProxyHeaders(t *testing.T) {
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil)
	r.Host = "127.0.0.1:55700"
	r.Header.Set("X-Forwarded-Proto", "https")
	r.Header.Set("X-Forwarded-Host", "launcher.samoy.love")

	if got := BaseURL(r); got != "https://launcher.samoy.love" {
		t.Fatalf("BaseURL = %q, want https://launcher.samoy.love", got)
	}
}

// Without proxy headers the request's own host is used — the dev case.
func TestBaseURLFallsBackToRequestHost(t *testing.T) {
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil)
	r.Host = "localhost:55700"
	if got := BaseURL(r); !strings.Contains(got, "localhost:55700") {
		t.Fatalf("BaseURL = %q", got)
	}
}

// The standard port must not be repeated in the URL: "https://host:443" is legal
// but breaks string comparisons against the configured base URL.
func TestBaseURLOmitsDefaultPort(t *testing.T) {
	r := httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/api/games", nil)
	r.Host = "launcher.samoy.love"
	r.Header.Set("X-Forwarded-Proto", "https")
	r.Header.Set("X-Forwarded-Port", "443")

	if got := BaseURL(r); strings.Contains(got, ":443") {
		t.Fatalf("BaseURL = %q, the default port must be omitted", got)
	}
}

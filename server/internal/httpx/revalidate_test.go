package httpx

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

// Pictures a client shows — game icons, news covers, hero art — used to be served
// with no-store, and that header forbids keeping the bytes at all: every launcher
// start re-downloaded all of them. Revalidate keeps them cacheable but always
// checked, so an unchanged picture crosses the wire once and a replaced one still
// arrives at once.
func TestRevalidateAllowsCachingButForcesCheck(t *testing.T) {
	h := Revalidate(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
	}))

	w := httptest.NewRecorder()
	h.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/assets/icon.png", nil))

	if cc := w.Header().Get("Cache-Control"); cc != "public, max-age=0, must-revalidate" {
		t.Errorf("Cache-Control = %q, want revalidation", cc)
	}
	if w.Header().Get("Pragma") != "" {
		t.Errorf("Pragma = %q, want none: it is the no-store leftover", w.Header().Get("Pragma"))
	}
}

// A handler wrapped for revalidation must not lose the validators the file server
// puts on the response: without them a client has nothing to ask about, and every
// request would answer 200 with a full body again.
func TestRevalidateKeepsValidators(t *testing.T) {
	h := Revalidate(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Last-Modified", "Wed, 21 Oct 2026 07:28:00 GMT")
		w.WriteHeader(http.StatusOK)
	}))

	w := httptest.NewRecorder()
	h.ServeHTTP(w, httptest.NewRequest(http.MethodGet, "/assets/icon.png", nil))

	if lm := w.Header().Get("Last-Modified"); lm == "" {
		t.Error("Last-Modified пропал: сверять клиенту будет нечем")
	}
}

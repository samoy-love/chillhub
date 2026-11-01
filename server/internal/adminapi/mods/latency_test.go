package mods

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"sync/atomic"
	"testing"
	"time"
)

func TestColdPanelDoesNotWaitForThunderstore(t *testing.T) {
	fs := newFakeStore(t)
	h, root := testHandlers(t, fs)
	publishPack(t, root, "lethal-company", "Team-Pack-1.0.0")
	release := make(chan struct{})
	var hits atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits.Add(1)
		<-release
		_ = json.NewEncoder(w).Encode(Package{Namespace: "Team", Name: "Pack", Latest: PackageVersion{VersionNumber: "2.0.0"}})
	}))
	defer srv.Close()
	defer func() {
		select {
		case <-release:
		default:
			close(release)
		}
	}()
	h.builder.Client = NewClient(srv.Client()).WithBases(srv.URL, srv.URL).WithInterval(time.Millisecond)
	// A forced refresh must also invalidate the assembled summary: otherwise
	// the next poll would return that old summary and stop checking early.
	h.sum.last = []ModsGameSummary{{GameID: "lethal-company", Latest: "old"}}
	h.sum.at = time.Now()

	for _, tc := range []struct {
		handler http.HandlerFunc
		query   string
	}{
		{h.SummaryHandler, "force=1"},
		{h.List, "gameId=lethal-company"},
		{h.SummaryHandler, ""},
	} {
		done := make(chan *httptest.ResponseRecorder, 1)
		go func() { done <- doGet(t, tc.handler, tc.query) }()
		select {
		case res := <-done:
			var state struct {
				Checking bool `json:"checking"`
			}
			if err := json.Unmarshal(res.Body.Bytes(), &state); err != nil || !state.Checking || res.Code != 200 {
				t.Fatalf("cold response must say checking: %d %s", res.Code, res.Body.String())
			}
		case <-time.After(time.Second):
			t.Fatal("local data waits for blocked Thunderstore")
		}
	}
	close(release)
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	if _, err := h.pkgs.pkg(ctx, h.builder.Client, "Team", "Pack"); err != nil {
		t.Fatal(err)
	}
	var summary Summary
	if err := json.Unmarshal(doGet(t, h.SummaryHandler, "").Body.Bytes(), &summary); err != nil {
		t.Fatal(err)
	}
	if summary.Checking || summary.Pending != 1 || len(summary.Mods) != 1 || summary.Mods[0].Latest != "2.0.0" {
		t.Fatalf("completed summary: %+v", summary)
	}
	var list struct {
		Checking bool          `json:"checking"`
		Updates  []updateCheck `json:"updates"`
	}
	if err := json.Unmarshal(doGet(t, h.List, "gameId=lethal-company").Body.Bytes(), &list); err != nil {
		t.Fatal(err)
	}
	if list.Checking || len(list.Updates) != 1 || list.Updates[0].Latest != "2.0.0" || hits.Load() != 1 {
		t.Fatalf("completed list %+v, upstream requests %d", list, hits.Load())
	}
}

func TestPollingReportsFailureInsteadOfRestartingForever(t *testing.T) {
	var hits atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits.Add(1)
		http.NotFound(w, r)
	}))
	defer srv.Close()
	cl := NewClient(srv.Client()).WithBases(srv.URL, srv.URL).WithInterval(time.Millisecond)
	var cache packageCache
	_, _ = cache.peek(context.Background(), cl, "Team", "Pack")
	cache.mu.Lock()
	e := cache.rows[PackageKey("Team", "Pack")]
	cache.mu.Unlock()
	select {
	case <-e.done:
	case <-time.After(time.Second):
		t.Fatal("lookup did not finish")
	}
	for range 3 {
		if _, err := cache.peek(context.Background(), cl, "Team", "Pack"); err == nil || errors.Is(err, errPackageChecking) {
			t.Fatalf("failed lookup hidden as pending/success: %v", err)
		}
	}
	if hits.Load() != 1 {
		t.Fatalf("polling retried failed request %d times", hits.Load())
	}
	cache.mu.Lock()
	e.at = time.Now().Add(-2 * time.Minute)
	cache.mu.Unlock()
	if _, err := cache.peek(context.Background(), cl, "Team", "Pack"); !errors.Is(err, errPackageChecking) {
		t.Fatalf("expired failure did not retry: %v", err)
	}
	// Let the bounded background request finish before closing the test server.
	_, _ = cache.pkg(context.Background(), cl, "Team", "Pack")
	if hits.Load() != 2 {
		t.Fatalf("retry requests %d", hits.Load())
	}
}

package mods

import (
	"bytes"
	"compress/gzip"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func serveIndexLinks(w http.ResponseWriter, r *http.Request) bool {
	if !strings.HasSuffix(r.URL.Path, "/api/v1/package-listing-index/") {
		return false
	}
	link := "http://" + r.Host + strings.Replace(r.URL.Path, "package-listing-index/", "package-listing-chunk/", 1)
	if err := json.NewEncoder(w).Encode([]string{link}); err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
	}
	return true
}

func gzipFixture(t *testing.T, data []byte) []byte {
	t.Helper()
	var b bytes.Buffer
	z := gzip.NewWriter(&b)
	if _, err := z.Write(data); err != nil {
		t.Fatal(err)
	}
	if err := z.Close(); err != nil {
		t.Fatal(err)
	}
	return b.Bytes()
}

func TestChunkedIndexRedirectGzipAndCompleteCache(t *testing.T) {
	var chunks []json.RawMessage
	if err := json.Unmarshal([]byte(communityListing), &chunks); err != nil {
		t.Fatal(err)
	}
	first := gzipFixture(t, append(append([]byte("["), chunks[0]...), ']'))
	second := gzipFixture(t, append(append([]byte("["), chunks[1]...), ']'))
	var broken atomic.Bool
	broken.Store(true)
	var hits atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits.Add(1)
		switch r.URL.Path {
		case "/c/lethal-company/api/v1/package-listing-index/":
			http.Redirect(w, r, "/links", http.StatusFound)
		case "/links":
			b, _ := json.Marshal([]string{"http://" + r.Host + "/one", "http://" + r.Host + "/two"})
			_, _ = w.Write(gzipFixture(t, b))
		case "/one":
			w.Header().Set("Content-Encoding", "gzip")
			_, _ = w.Write(first)
		case "/two":
			if broken.Load() {
				_, _ = w.Write([]byte("["))
				return
			}
			_, _ = w.Write(second)
		default:
			t.Errorf("unexpected endpoint %s", r.URL.Path)
			http.NotFound(w, r)
		}
	}))
	defer srv.Close()
	c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	if idx, err := c.FetchCommunityIndex(context.Background(), "lethal-company"); err == nil || idx != nil {
		t.Fatalf("partial index accepted: %v %v", idx, err)
	}
	if c.cachedIndex("lethal-company") != nil {
		t.Fatal("partial index cached")
	}
	broken.Store(false)
	idx, err := c.FetchCommunityIndex(context.Background(), "lethal-company")
	if err != nil {
		t.Fatal(err)
	}
	if idx.Len() != 2 || !idx.Serves("swuff-star-ConfigurableCrafting-1.0.0") {
		t.Fatalf("lost second chunk: %+v", idx)
	}
	v, ok := idx.Lookup("Team-Pack-1.0.0")
	if !ok || v.FileSize != 1024 || len(v.Dependencies) != 1 {
		t.Fatalf("lost metadata: %+v", v)
	}
	before := hits.Load()
	cached, err := c.FetchCommunityIndex(context.Background(), "lethal-company")
	if err != nil || cached != idx || hits.Load() != before {
		t.Fatal("complete cache not reused")
	}
}

func TestIndexRejectsUntrustedLinksAndRedirects(t *testing.T) {
	var forbidden atomic.Int32
	other := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { forbidden.Add(1) }))
	defer other.Close()
	for _, redirect := range []bool{false, true} {
		t.Run(strconv.FormatBool(redirect), func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if redirect {
					http.Redirect(w, r, other.URL, http.StatusFound)
					return
				}
				_ = json.NewEncoder(w).Encode([]string{other.URL})
			}))
			defer srv.Close()
			c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
			if _, err := c.FetchCommunityIndex(context.Background(), "peak"); err == nil {
				t.Fatal("foreign URL accepted")
			}
			if forbidden.Load() != 0 {
				t.Fatal("request reached untrusted host")
			}
		})
	}
	c := NewClient(nil)
	for _, raw := range []string{"http://ccdn.thunderstore.io/live/blob-storage/a", "https://ccdn.thunderstore.io.evil/live/blob-storage/a", "https://user@ccdn.thunderstore.io/live/blob-storage/a", "https://ccdn.thunderstore.io:444/live/blob-storage/a", "https://ccdn.thunderstore.io/other/a", "/relative", "https://ccdn.thunderstore.io/live/blob-storage/a#fragment"} {
		if c.allowedIndexURL(raw) {
			t.Errorf("accepted %q", raw)
		}
	}
	for _, raw := range []string{"https://ccdn.thunderstore.io/live/blob-storage/a", "https://gcdn.thunderstore.io/live/blob-storage/a"} {
		if !c.allowedIndexURL(raw) {
			t.Errorf("rejected %q", raw)
		}
	}
}

func TestIndexBlobBoundsAndIntegrity(t *testing.T) {
	valid := gzipFixture(t, []byte(strings.Repeat("x", 1024)))
	corrupt := append([]byte(nil), valid...)
	corrupt[len(corrupt)-1] ^= 1
	for name, data := range map[string][]byte{"expanded limit": valid, "crc": corrupt, "truncated gzip": valid[:len(valid)-5]} {
		t.Run(name, func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write(data) }))
			defer srv.Close()
			c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
			limit := int64(2048)
			if name == "expanded limit" {
				limit = 64
			}
			if _, err := c.fetchIndexBlob(context.Background(), srv.URL, limit); err == nil {
				t.Fatal("invalid blob accepted")
			}
		})
	}
	for _, body := range []string{"null", "{}", "[", communityListing[:len(communityListing)-1], communityListing + "{}", communityListing + "garbage"} {
		idx := &CommunityIndex{versions: map[string]IndexedVersion{}, packages: map[string]bool{}}
		if err := idx.readChunk([]byte(body)); err == nil {
			t.Errorf("invalid chunk accepted: %.30s", body)
		}
	}
}

func TestIndexRetriesRateLimitAndHonorsCancellation(t *testing.T) {
	var hits atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if hits.Add(1) == 1 {
			w.WriteHeader(http.StatusTooManyRequests)
			return
		}
		_, _ = w.Write([]byte("[]"))
	}))
	defer srv.Close()
	c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	b, err := c.fetchIndexBlob(context.Background(), srv.URL, 100)
	if err != nil || string(b) != "[]" || hits.Load() != 2 {
		t.Fatalf("retry: %q %v hits=%d", b, err, hits.Load())
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := c.fetchIndexBlob(ctx, srv.URL, 100); err == nil {
		t.Fatal("cancellation ignored")
	}
	if hits.Load() != 2 {
		t.Fatal("canceled request reached server")
	}
}

func TestIndexRejectsInvalidLinkLists(t *testing.T) {
	for _, body := range []string{"null", "[]", "{}", "[42]", "garbage"} {
		t.Run(body, func(t *testing.T) {
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { _, _ = w.Write([]byte(body)) }))
			defer srv.Close()
			c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
			if _, err := c.FetchCommunityIndex(context.Background(), "peak"); err == nil {
				t.Fatal("invalid links accepted")
			}
		})
	}
}

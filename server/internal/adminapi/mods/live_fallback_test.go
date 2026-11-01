package mods

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"slices"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

type unavailableIndexTransport struct {
	requests atomic.Int64
}

func (tr *unavailableIndexTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	if strings.Contains(r.URL.Path, "/api/v1/package-listing-index/") {
		tr.requests.Add(1)
		return &http.Response{
			StatusCode: http.StatusNotFound, Header: make(http.Header),
			Body: io.NopCloser(strings.NewReader("index unavailable")), Request: r,
		}, nil
	}
	return http.DefaultTransport.RoundTrip(r)
}

// Exercise the actual builder with a cold cache and an unavailable index.
// Only Resolve is called: production sources and archives are never published.
func TestLiveCurrentModpacksWithoutIndex(t *testing.T) {
	requireNetwork(t)
	path := os.Getenv("CHILLHUB_MODPACK_SOURCES")
	if path == "" {
		t.Skip("CHILLHUB_MODPACK_SOURCES is required")
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var packs []struct {
		GameID    string   `json:"gameId"`
		Community string   `json:"community"`
		Roots     []string `json:"roots"`
		Tree      []string `json:"tree"`
		Foreign   []string `json:"foreign"`
		Loader    string   `json:"loader"`
	}
	if err := json.Unmarshal(data, &packs); err != nil {
		t.Fatal(err)
	}
	if len(packs) != 5 {
		t.Fatalf("expected five active modpacks, got %d", len(packs))
	}
	ctx, cancel := context.WithTimeout(context.Background(), 25*time.Minute)
	defer cancel()
	for _, p := range packs {
		t.Run(p.GameID, func(t *testing.T) {
			tr := &unavailableIndexTransport{}
			c := NewClient(&http.Client{Transport: tr})
			root := t.TempDir()
			b := &Builder{Client: c, Eco: NewEcosystemCache(c, root), Cache: NewArchiveCache(root), Root: root}
			ns, name, version, ok := SplitDependency(p.Roots[0])
			if !ok {
				t.Fatal("invalid root")
			}
			plan, err := b.Resolve(ctx, Request{
				GameID: p.GameID, EcosystemGame: p.Community, Kind: SourceThunderstore,
				Namespace: ns, Name: name, Version: version, Roots: p.Roots,
			})
			if err != nil {
				t.Fatal(err)
			}
			var tree []string
			for _, v := range plan.Packages {
				tree = append(tree, v.FullName)
			}
			slices.Sort(tree)
			slices.Sort(p.Tree)
			slices.Sort(p.Foreign)
			if tr.requests.Load() != 1 || !slices.Equal(tree, p.Tree) || !slices.Equal(plan.Foreign, p.Foreign) ||
				plan.Loader != p.Loader || len(plan.Missing)+len(plan.ExtraLoaders) != 0 {
				t.Fatalf("fallback changed: tree=%v foreign=%v missing=%v extra=%v loader=%s indexRequests=%d",
					tree, plan.Foreign, plan.Missing, plan.ExtraLoaders, plan.Loader, tr.requests.Load())
			}
			t.Logf("index unavailable; unchanged %d packages; loader=%s; excluded=%v; archive estimate=%d",
				len(tree), plan.Loader, plan.Foreign, plan.TotalBytes)
		})
	}
}

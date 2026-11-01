package mods

import (
	"context"
	"encoding/json"
	"io"
	"os"
	"slices"
	"testing"
	"time"
)

// CHILLHUB_MODPACK_SOURCES points to a read-only snapshot of active sources,
// enriched with gameId/community. The test never builds or publishes a version.
// It compares complete dependency trees, then downloads the root and loader.
func TestLiveCurrentModpacksChunkedIndex(t *testing.T) {
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
	if len(packs) == 0 {
		t.Fatal("empty modpack snapshot")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Minute)
	defer cancel()
	c := NewClient(nil)
	eco, err := NewEcosystemCache(c, t.TempDir()).Get(ctx)
	if err != nil {
		t.Fatal(err)
	}
	for _, p := range packs {
		t.Run(p.GameID, func(t *testing.T) {
			idx, err := c.FetchCommunityIndex(ctx, p.Community)
			if err != nil {
				t.Fatal(err)
			}
			res, err := c.ResolveListWithIndex(ctx, eco, p.Roots, nil, idx)
			if err != nil {
				t.Fatal(err)
			}
			var tree []string
			for _, v := range res.Packages {
				tree = append(tree, v.FullName)
			}
			slices.Sort(tree)
			slices.Sort(p.Tree)
			slices.Sort(p.Foreign)
			if len(res.Missing)+len(res.ExtraLoaders) != 0 {
				t.Fatalf("unresolved packages: missing=%v extra loaders=%v", res.Missing, res.ExtraLoaders)
			}
			if !slices.Equal(tree, p.Tree) || !slices.Equal(res.Foreign, p.Foreign) || res.Loader != p.Loader {
				t.Fatalf("tree changed: got %v foreign=%v missing=%v loader=%s; want %v foreign=%v loader=%s", tree, res.Foreign, res.Missing, res.Loader, p.Tree, p.Foreign, p.Loader)
			}
			for _, v := range res.Packages {
				if !v.IsLoader && !slices.Contains(p.Roots, v.FullName) {
					continue
				}
				n, err := c.Download(ctx, v.Ref(), io.Discard)
				if err != nil {
					t.Fatal(err)
				}
				meta, ok := idx.Lookup(v.FullName)
				if !ok || n != meta.FileSize {
					t.Fatalf("%s: downloaded %d expected %d", v.FullName, n, meta.FileSize)
				}
			}
			t.Logf("unchanged: %d packages; loader %s; root and loader downloads OK", len(tree), res.Loader)
		})
	}
}

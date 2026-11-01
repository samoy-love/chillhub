package mods

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"
	"time"
)

func TestBuildIndexOutageExcludesForeignSubtree(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.deps["Author-CoolMod-1.0.0"] = append(fs.deps["Author-CoolMod-1.0.0"], "OtherGame-Mod-1.0.0")
	fs.add("OtherGame-Mod-1.0.0", []string{"OtherGame-Library-1.0.0"}, map[string]string{"Foreign.dll": "foreign"})
	fs.add("OtherGame-Library-1.0.0", nil, map[string]string{"Library.dll": "foreign"})
	for _, full := range []string{"Team-Pack-1.0.0", "Author-CoolMod-1.0.0", "BepInEx-BepInExPack-5.4.2305"} {
		fs.community[full] = true
	}
	fs.indexStatus = http.StatusNotFound
	b, _ := testBuilder(t, fs)
	src, err := b.Build(context.Background(), thunderstoreRequest(), false, nil)
	if err != nil {
		t.Fatal(err)
	}
	if len(src.Tree) != 3 || !slices.Equal(src.Foreign, []string{"OtherGame-Mod-1.0.0"}) {
		t.Fatalf("tree=%v foreign=%v", src.Tree, src.Foreign)
	}
	for _, full := range []string{"OtherGame-Mod-1.0.0", "OtherGame-Library-1.0.0"} {
		if fs.apiHits[full] != 0 || fs.hits[full] != 0 {
			t.Fatalf("foreign package fetched: %s", full)
		}
	}
}

func TestCommunityFallbackUsesApprovedNamespaceSplit(t *testing.T) {
	var versionPath string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/api/cyberstorm/listing/game/swuff/star-Mod/":
			http.NotFound(w, r)
		case "/api/cyberstorm/listing/game/swuff-star/Mod/":
			_ = json.NewEncoder(w).Encode(communityListingIdentity{"game", "swuff-star", "Mod"})
		case "/api/experimental/package/swuff-star/Mod/1.0.0/":
			versionPath = r.URL.Path
			_ = json.NewEncoder(w).Encode(PackageVersion{FullName: "swuff-star-Mod-1.0.0"})
		default:
			t.Errorf("unapproved request: %s", r.URL.Path)
			http.Error(w, "unexpected", http.StatusBadRequest)
		}
	}))
	defer srv.Close()
	c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL).WithInterval(time.Millisecond)
	v, foreign, err := c.getCommunityDependency(context.Background(), "game", "swuff-star-Mod-1.0.0")
	if err != nil || foreign || v == nil || v.FullName != "swuff-star-Mod-1.0.0" || versionPath == "" {
		t.Fatalf("v=%v foreign=%v err=%v path=%s", v, foreign, err, versionPath)
	}
}

func TestCommunityFallbackFailsClosed(t *testing.T) {
	for _, tc := range []struct {
		name    string
		status  int
		body    string
		foreign bool
		wantErr bool
		missing bool
	}{
		{"foreign", 404, "", true, false, false},
		{"forbidden", 403, "", false, true, false},
		{"unavailable", 503, "", false, true, false},
		{"invalid_json", 200, "{", false, true, false},
		{"empty_json", 200, "{}", false, true, false},
		{"wrong_community", 200, `{"community_identifier":"other","namespace":"Team","name":"Mod"}`, false, true, false},
		{"wrong_namespace", 200, `{"community_identifier":"game","namespace":"Other","name":"Mod"}`, false, true, false},
		{"wrong_name", 200, `{"community_identifier":"game","namespace":"Team","name":"Other"}`, false, true, false},
		{"missing_version", 200, `{"community_identifier":"game","namespace":"Team","name":"Mod"}`, false, true, true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			versionHits := 0
			srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if strings.HasPrefix(r.URL.Path, "/api/experimental/") {
					versionHits++
					http.NotFound(w, r)
					return
				}
				w.WriteHeader(tc.status)
				_, _ = w.Write([]byte(tc.body))
			}))
			defer srv.Close()
			c := NewClient(srv.Client()).WithBases(srv.URL, srv.URL).WithInterval(time.Millisecond)
			// A cached version must not bypass a failed membership check.
			if !tc.missing {
				c.WithMetaCache(t.TempDir())
				c.metaWrite("Team-Mod-1.0.0", &PackageVersion{FullName: "Team-Mod-1.0.0"})
			}
			v, foreign, err := c.getCommunityDependency(context.Background(), "game", "Team-Mod-1.0.0")
			if v != nil || foreign != tc.foreign || (err != nil) != tc.wantErr || errors.Is(err, ErrNotFound) != tc.missing {
				t.Fatalf("v=%v foreign=%v err=%v", v, foreign, err)
			}
			if !tc.missing && versionHits != 0 {
				t.Fatal("unchecked version requested")
			}
			if tc.missing && versionHits != 1 {
				t.Fatalf("version requests=%d", versionHits)
			}
		})
	}
}

func TestCommunityFallbackValidationAndCancellation(t *testing.T) {
	c := NewClient(nil)
	for _, community := range []string{"", "../game"} {
		if _, err := c.ResolveListInCommunity(context.Background(), &Ecosystem{}, []string{"Root-Pack-1.0.0"}, nil, community, nil); err == nil {
			t.Fatalf("accepted community %q", community)
		}
	}
	if _, err := c.ResolveListInCommunity(context.Background(), &Ecosystem{}, []string{"Root-Pack-1.0.0"}, nil, "game", &CommunityIndex{community: "other"}); err == nil {
		t.Fatal("accepted wrong index")
	}
	if _, _, err := c.getCommunityDependency(context.Background(), "game", "broken"); err == nil {
		t.Fatal("accepted malformed dependency")
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, foreign, err := c.getCommunityDependency(ctx, "game", "Team-Mod-1.0.0"); !errors.Is(err, context.Canceled) || foreign {
		t.Fatalf("foreign=%v err=%v", foreign, err)
	}
}

func TestCommunityFallbackKeepsMissingVersionSeparate(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.deps["Team-Pack-1.0.0"] = []string{"Author-CoolMod-9.0.0"}
	fs.community["Author-CoolMod-1.0.0"] = true
	c := NewClient(fs.Client()).WithBases(fs.URL, fs.URL).WithInterval(time.Millisecond)
	for _, idx := range []*CommunityIndex{nil, {community: "lethal-company"}} {
		res, err := c.ResolveListInCommunity(context.Background(), &Ecosystem{},
			[]string{"Team-Pack-1.0.0"}, nil, "lethal-company", idx)
		if err != nil || res == nil {
			t.Fatal(err)
		}
		if len(res.Packages) != 1 || len(res.Foreign) != 0 ||
			!slices.Equal(res.Missing, []string{"Author-CoolMod-9.0.0"}) {
			t.Fatalf("resolution=%+v", res)
		}
	}
}

func TestUnscopedResolveWrappersRetainPinnedTree(t *testing.T) {
	env := newResolveEnv(t, map[string][]string{
		"Team-Pack-1.0.0": {"Author-Mod-2.0.0"}, "Author-Mod-2.0.0": {},
	}, "[]")
	for _, withProgress := range []bool{false, true} {
		var res *Resolution
		var err error
		if withProgress {
			res, err = env.client.ResolveListWith(context.Background(), &Ecosystem{}, []string{"Team-Pack-1.0.0"}, nil)
		} else {
			res, err = env.client.ResolveList(context.Background(), &Ecosystem{}, []string{"Team-Pack-1.0.0"})
		}
		if err != nil || res == nil {
			t.Fatal(err)
		}
		if len(res.Packages) != 2 || res.Packages[1].FullName != "Author-Mod-2.0.0" || len(res.Missing) != 0 {
			t.Fatalf("resolution=%+v", res)
		}
	}
}

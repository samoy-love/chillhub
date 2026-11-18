package builds

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestSmallFileGroupsRespectLimitsAndKeepOriginalIndices(t *testing.T) {
	files := []manifestFile{{Size: 0}, {Size: maxSmallFileBytes + 1}}
	for range 9 {
		files = append(files, manifestFile{Size: maxSmallFileBytes})
	}
	for range 136 {
		files = append(files, manifestFile{Size: 1})
	}
	groups := smallFileGroups(files)
	requirePackCounts(t, groups, []int{8, 20, 20, 20, 20, 19, 19, 19})
	next := 2
	for _, group := range groups {
		var size int64
		for _, index := range group {
			if index != next {
				t.Fatalf("lost manifest order at %d: %d", next, index)
			}
			next++
			size += files[index].Size
		}
		if size > maxPackBytes {
			t.Fatalf("pack size = %d, limit %d", size, maxPackBytes)
		}
	}
	if got := smallFileGroups(files[:9]); len(got) != 0 {
		t.Fatalf("a group of seven is too small: %v", got)
	}
}

func TestSmallFileGroupsAdaptToCatalogSize(t *testing.T) {
	for _, tc := range []struct {
		name  string
		count int
		size  int64
		want  []int
	}{
		{name: "one small pack", count: 15, size: 4096, want: []int{15}},
		{name: "two minimum packs", count: 16, size: 4096, want: []int{8, 8}},
		{name: "does not leave a tiny tail", count: 17, size: 4096, want: []int{9, 8}},
		{name: "eight balanced packs", count: 240, size: 4096, want: []int{30, 30, 30, 30, 30, 30, 30, 30}},
		{name: "enough packs for a large tree", count: 1200, size: 4096, want: []int{120, 120, 120, 120, 120, 120, 120, 120, 120, 120}},
		{name: "byte-constrained last group", count: 73, size: maxSmallFileBytes, want: []int{8, 8, 8, 8, 8, 8, 8, 8, 8}},
	} {
		t.Run(tc.name, func(t *testing.T) {
			files := make([]manifestFile, tc.count)
			for i := range files {
				files[i].Size = tc.size
			}
			requirePackCounts(t, smallFileGroups(files), tc.want)
		})
	}
}

func TestSmallFileGroupsUseEarlierSpaceForAShortTail(t *testing.T) {
	files := make([]manifestFile, 18)
	for i := range files {
		files[i].Size = maxSmallFileBytes
		if i < 9 {
			files[i].Size = 1
		}
	}
	want := [][]int{{0, 1, 2, 3, 4, 5, 6, 7, 8, 17}, {9, 10, 11, 12, 13, 14, 15, 16}}
	if got := smallFileGroups(files); !reflect.DeepEqual(got, want) {
		t.Fatalf("groups = %v, want %v", got, want)
	}
}

func requirePackCounts(t *testing.T, groups [][]int, want []int) {
	t.Helper()
	got := make([]int, len(groups))
	for i, group := range groups {
		got[i] = len(group)
	}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("pack sizes = %v, want %v", got, want)
	}
}

func TestPublishedPacksMatchTheWireManifestAndOriginalFiles(t *testing.T) {
	for _, namespace := range []Namespace{NamespaceGame, NamespaceMods} {
		t.Run(string(namespace), func(t *testing.T) {
			h := New(t.TempDir())
			stage, err := h.StageTree(namespace, "demo", "1.0")
			if err != nil {
				t.Fatal(err)
			}
			for i := range 134 {
				writeFile(t, filepath.Join(stage.FilesRoot, fmt.Sprintf("content/%03d", i)), []byte(fmt.Sprintf("member %d", i)))
			}
			writeFile(t, filepath.Join(stage.FilesRoot, "huge.bin"), make([]byte, maxSmallFileBytes+1))
			res, err := h.Publish(stage, true, nil)
			if err != nil {
				t.Fatal(err)
			}
			raw, err := os.ReadFile(res.ManifestPath)
			if err != nil {
				t.Fatal(err)
			}
			var m manifest
			if err = json.Unmarshal(raw, &m); err != nil {
				t.Fatal(err)
			}
			version := filepath.Join(h.ContentDirFor(namespace, "demo"), "1.0")
			requirePublishedPacksMatchFiles(t, version, m)
			repeated, err := writeSmallPacks(version, m)
			if err != nil || !reflect.DeepEqual(repeated, m.SmallPacks) {
				t.Fatalf("regeneration changed the manifest: %v", err)
			}
			first := m.Files[m.SmallPacks[0].Files[0]]
			writeFile(t, filepath.Join(version, "files", filepath.FromSlash(first.Path)), bytes.Repeat([]byte("x"), int(first.Size)))
			if _, err := writeSmallPacks(version, m); err == nil {
				t.Fatal("accepted a changed member of the same length")
			}
			m.GameID = LauncherGameID
			if packs, err := writeSmallPacks(version, m); err != nil || len(packs) != 0 {
				t.Fatalf("updater payload unexpectedly packed: %v, %v", packs, err)
			}
		})
	}
}

func requirePublishedPacksMatchFiles(t *testing.T, version string, m manifest) {
	t.Helper()
	var groups [][]int
	for _, pack := range m.SmallPacks {
		groups = append(groups, pack.Files)
		var want []byte
		for _, index := range pack.Files {
			data, err := os.ReadFile(filepath.Join(version, "files", filepath.FromSlash(m.Files[index].Path)))
			if err != nil {
				t.Fatal(err)
			}
			want = append(want, data...)
		}
		hash := sha256.Sum256(want)
		if pack.SHA256 != hex.EncodeToString(hash[:]) {
			t.Fatal("wire digest differs from published member bytes")
		}
		got, err := os.ReadFile(filepath.Join(version, "packs", pack.SHA256+".bin"))
		if err != nil || !bytes.Equal(got, want) {
			t.Fatalf("packed bytes differ: %v", err)
		}
	}
	requirePackCounts(t, groups, []int{17, 17, 17, 17, 17, 17, 16, 16})
}

func TestPackFailureKeepsPreviouslyPublishedVersion(t *testing.T) {
	h := New(t.TempDir())
	live, err := h.StageTree(NamespaceGame, "demo", "1")
	if err != nil {
		t.Fatal(err)
	}
	writeFile(t, filepath.Join(live.FilesRoot, "original.txt"), []byte("live"))
	published, err := h.Publish(live, true, nil)
	if err != nil {
		t.Fatal(err)
	}
	before, err := os.ReadFile(published.ManifestPath)
	if err != nil {
		t.Fatal(err)
	}
	stage, err := h.StageTree(NamespaceGame, "demo", "1")
	if err != nil {
		t.Fatal(err)
	}
	for i := range minPackFiles {
		writeFile(t, filepath.Join(stage.FilesRoot, fmt.Sprintf("f%d", i)), []byte{byte(i)})
	}
	files, dirs, err := walkManifest(stage.FilesRoot, nil)
	if err != nil {
		t.Fatal(err)
	}
	version := filepath.Join(h.ContentDirFor(NamespaceGame, "demo"), "1")
	// Keep SHA-256 correct. The packer must check BLAKE3 independently.
	files[0].Blake3 = files[1].Blake3
	m := manifest{Version: "1", GameID: "demo", Files: files, EmptyDirs: dirs}
	_, _, err = h.publishTree(stage.Dir, version, filepath.Dir(published.ManifestPath), m, true)
	if err == nil {
		t.Fatal("published a pack with inconsistent member hashes")
	}
	after, err := os.ReadFile(published.ManifestPath)
	if err != nil || !bytes.Equal(after, before) {
		t.Fatalf("previous manifest changed: %v", err)
	}
	liveFile, err := os.ReadFile(filepath.Join(version, "files", "original.txt"))
	if err != nil || string(liveFile) != "live" {
		t.Fatalf("previous content changed: %v", err)
	}
}

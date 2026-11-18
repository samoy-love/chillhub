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
	if len(groups) != 3 || len(groups[0]) != 8 || len(groups[1]) != 128 || len(groups[2]) != 9 {
		t.Fatalf("unexpected groups: %v", groups)
	}
	for i, index := range append(append(groups[0], groups[1]...), groups[2]...) {
		if index != i+2 {
			t.Fatalf("lost manifest order at %d: %d", i, index)
		}
	}
	if got := smallFileGroups(files[:9]); len(got) != 0 {
		t.Fatalf("a group of seven is too small: %v", got)
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
			if err = json.Unmarshal(raw, &m); err != nil || len(m.SmallPacks) != 1 || len(m.SmallPacks[0].Files) != 128 {
				t.Fatalf("unexpected packs %v (error %v)", m.SmallPacks, err)
			}
			version := filepath.Join(h.ContentDirFor(namespace, "demo"), "1.0")
			var want []byte
			for _, index := range m.SmallPacks[0].Files {
				data, err := os.ReadFile(filepath.Join(version, "files", filepath.FromSlash(m.Files[index].Path)))
				if err != nil {
					t.Fatal(err)
				}
				want = append(want, data...)
			}
			hash := sha256.Sum256(want)
			if m.SmallPacks[0].SHA256 != hex.EncodeToString(hash[:]) {
				t.Fatal("wire digest differs from published member bytes")
			}
			got, err := os.ReadFile(filepath.Join(version, "packs", m.SmallPacks[0].SHA256+".bin"))
			if err != nil || !bytes.Equal(got, want) {
				t.Fatalf("packed bytes differ: %v", err)
			}
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

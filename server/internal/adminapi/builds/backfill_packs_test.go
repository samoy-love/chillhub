package builds

import (
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"reflect"
	"strconv"
	"testing"
	"time"
)

const packBackfillVersion = "1.2.3"

func legacySmallFiles() map[string][]byte {
	files := make(map[string][]byte, minPackFiles+1)
	for i := range minPackFiles {
		files[fmt.Sprintf("config/settings-%02d.ini", i)] = []byte(fmt.Sprintf("quality=%d", i))
	}
	files["bigger.dat"] = bytes.Repeat([]byte{0xab}, maxSmallFileBytes+1)
	return files
}

func packBackfillPaths(h *Handlers, ns Namespace) (manifestPath, versionDir string) {
	return filepath.Join(h.ManifestsDirFor(ns, "g"), packBackfillVersion+".json"), filepath.Join(h.ContentDirFor(ns, "g"), packBackfillVersion)
}

func readBackfillFixture(t *testing.T, path string) []byte {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return data
}

func TestBackfillSmallPacksPreservesThePublishedRelease(t *testing.T) {
	for _, ns := range []Namespace{NamespaceGame, NamespaceMods} {
		t.Run(string(ns), func(t *testing.T) {
			root := t.TempDir()
			h := New(root)
			files := legacySmallFiles()
			publishedBeforeBlocks(t, root, ns, "g", packBackfillVersion, files)
			path, versionDir := packBackfillPaths(h, ns)
			before := readManifestFile(t, path)
			latest := filepath.Join(h.ManifestsDirFor(ns, "g"), "latest.json")
			pointer := []byte("{ \"version\": \"1.2.3\" }\n")
			writeFile(t, latest, pointer)

			res, err := h.BackfillSmallPacks(ns, "g", packBackfillVersion)
			if err != nil || res != (SmallPacksBackfillResult{Packs: 1, Files: minPackFiles, ManifestUpdated: true}) {
				t.Fatalf("backfill = %+v, %v", res, err)
			}
			after := readManifestFile(t, path)
			if len(after.SmallPacks) != 1 {
				t.Fatalf("published packs = %+v", after.SmallPacks)
			}
			pack := after.SmallPacks[0]
			var want []byte
			for _, index := range pack.Files {
				want = append(want, files[after.Files[index].Path]...)
			}
			sum := sha256.Sum256(want)
			if pack.SHA256 != hex.EncodeToString(sum[:]) ||
				!bytes.Equal(readBackfillFixture(t, filepath.Join(versionDir, "packs", pack.SHA256+".bin")), want) {
				t.Fatal("pack digest or payload does not match the published files")
			}
			after.SmallPacks = nil
			if !reflect.DeepEqual(after, before) {
				t.Fatalf("backfill modified other manifest metadata: %+v", after)
			}
			if !bytes.Equal(readBackfillFixture(t, latest), pointer) {
				t.Fatal("backfill rewrote the version pointer")
			}
		})
	}
}

func TestBackfillSmallPacksRepairsObjectsWithoutRewritingTheManifest(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	publishedBeforeBlocks(t, root, NamespaceGame, "g", packBackfillVersion, legacySmallFiles())
	path, versionDir := packBackfillPaths(h, NamespaceGame)
	if _, err := h.BackfillSmallPacks(NamespaceGame, "g", packBackfillVersion); err != nil {
		t.Fatal(err)
	}
	pack := readManifestFile(t, path).SmallPacks[0]
	packPath := filepath.Join(versionDir, "packs", pack.SHA256+".bin")
	manifestBytes := readBackfillFixture(t, path)
	packBytes := readBackfillFixture(t, packPath)

	stamp := time.Date(2000, 1, 1, 0, 0, 0, 0, time.UTC)
	for _, target := range []string{path, packPath} {
		if err := os.Chtimes(target, stamp, stamp); err != nil {
			t.Fatal(err)
		}
	}
	check := func() {
		t.Helper()
		result, err := h.BackfillSmallPacks(NamespaceGame, "g", packBackfillVersion)
		if err != nil || result != (SmallPacksBackfillResult{Packs: 1, Files: minPackFiles}) {
			t.Fatalf("repeat backfill = %+v, %v", result, err)
		}
		if !bytes.Equal(readBackfillFixture(t, path), manifestBytes) ||
			!bytes.Equal(readBackfillFixture(t, packPath), packBytes) {
			t.Fatal("repeated backfill did not restore the same objects")
		}
		info, err := os.Stat(path)
		if err != nil || !info.ModTime().Equal(stamp) {
			t.Fatalf("manifest was replaced: %v, %v", info, err)
		}
	}

	check()
	info, err := os.Stat(packPath)
	if err != nil || !info.ModTime().Equal(stamp) {
		t.Fatalf("verified pack was replaced: %v, %v", info, err)
	}
	corrupt := append([]byte(nil), packBytes...)
	corrupt[0] ^= 0xff
	writeFile(t, packPath, corrupt)
	check()
	if err := os.Remove(packPath); err != nil {
		t.Fatal(err)
	}
	check()
}

func TestBackfillSmallPacksRejectsMismatchedPayload(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	publishedBeforeBlocks(t, root, NamespaceGame, "g", packBackfillVersion, legacySmallFiles())
	path, versionDir := packBackfillPaths(h, NamespaceGame)
	before := readBackfillFixture(t, path)
	writeFile(t, filepath.Join(versionDir, "files", "config", "settings-00.ini"), []byte("quality=9"))
	if _, err := h.BackfillSmallPacks(NamespaceGame, "g", packBackfillVersion); err == nil {
		t.Fatal("backfill accepted bytes that differ from the published digest")
	}
	if !bytes.Equal(readBackfillFixture(t, path), before) {
		t.Fatal("backfill rewrote the manifest despite mismatched bytes")
	}
}

func TestBackfillSmallPacksLeavesRepublishedMetadataIntact(t *testing.T) {
	for _, existing := range []bool{false, true} {
		t.Run(strconv.FormatBool(existing), func(t *testing.T) {
			root := t.TempDir()
			h := New(root)
			publishedBeforeBlocks(t, root, NamespaceGame, "g", packBackfillVersion, legacySmallFiles())
			if existing {
				if _, err := h.BackfillSmallPacks(NamespaceGame, "g", packBackfillVersion); err != nil {
					t.Fatal(err)
				}
			}
			path, _ := packBackfillPaths(h, NamespaceGame)
			newManifest := readManifestFile(t, path)
			newManifest.BuildID = "build-2"
			republished, err := json.Marshal(newManifest)
			if err != nil {
				t.Fatal(err)
			}
			oldHook := backfillHashed
			t.Cleanup(func() { backfillHashed = oldHook })
			backfillHashed = func() { writeFile(t, path, republished) }

			_, err = h.BackfillSmallPacks(NamespaceGame, "g", packBackfillVersion)
			if !errors.Is(err, ErrManifestChanged) {
				t.Fatalf("republish error = %v, want ErrManifestChanged", err)
			}
			if !bytes.Equal(readBackfillFixture(t, path), republished) {
				t.Fatal("backfill overwrote the republished manifest")
			}
		})
	}
}

func TestBackfillSmallPacksRejectsOutsideContentTree(t *testing.T) {
	h := New(t.TempDir())
	publishedBeforeBlocks(t, h.root, NamespaceGame, "g", packBackfillVersion, legacySmallFiles())
	path, versionDir := packBackfillPaths(h, NamespaceGame)
	outside := filepath.Join(t.TempDir(), "version")
	if err := os.Rename(versionDir, outside); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(outside, versionDir); err != nil {
		t.Skipf("cannot create fixture symlink: %v", err)
	}
	before := readBackfillFixture(t, path)
	if _, err := h.BackfillSmallPacks(NamespaceGame, "g", packBackfillVersion); err == nil {
		t.Fatal("backfill followed a version directory outside the content root")
	}
	if !bytes.Equal(readBackfillFixture(t, path), before) {
		t.Fatal("backfill rewrote the manifest despite the invalid directory")
	}
	if _, err := os.Stat(filepath.Join(outside, "packs")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("outside pack directory was reached: %v", err)
	}
}

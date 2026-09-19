package builds

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"testing"
)

// publishedBeforeBlocks раскладывает версию так, как её оставил сервер до
// появления хешей блоков: файлы на месте, в манифесте только полные хеши.
func publishedBeforeBlocks(t *testing.T, root string, ns Namespace, gid, ver string, files map[string][]byte) manifest {
	t.Helper()
	h := New(root)
	filesRoot := filepath.Join(h.ContentDirFor(ns, gid), ver, "files")
	for rel, data := range files {
		writeFile(t, filepath.Join(filesRoot, filepath.FromSlash(rel)), data)
	}
	list, dirs, err := scanManifest(filesRoot)
	if err != nil {
		t.Fatal(err)
	}
	for i := range list {
		list[i].Blocks = ""
	}
	m := manifest{Version: ver, BuildID: "build-1", GameID: gid, CreatedAt: "2026-01-02T03:04:05Z", Files: list, EmptyDirs: dirs}
	if _, _, err := h.writeManifestTo(h.ManifestsDirFor(ns, gid), m, false); err != nil {
		t.Fatal(err)
	}
	return m
}

func readManifestFile(t *testing.T, path string) manifest {
	t.Helper()
	b, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	var m manifest
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatal(err)
	}
	return m
}

func TestBackfillAddsBlocksAndKeepsEverythingElse(t *testing.T) {
	root := t.TempDir()
	big := blockPattern(3*BlockSize + 100)
	before := publishedBeforeBlocks(t, root, NamespaceGame, "bodycam", "0.8.12.19193", map[string][]byte{
		"Paks/big.pak": big,
		"cfg.ini":      []byte("a=b"),
	})
	h := New(root)

	res, err := h.BackfillBlocks(NamespaceGame, "bodycam", "0.8.12.19193")
	if err != nil {
		t.Fatal(err)
	}
	if res.Files != 1 || res.Bytes != int64(len(big)) || res.Skipped != "" {
		t.Fatalf("result = %+v, want one file of %d bytes", res, len(big))
	}

	after := readManifestFile(t, filepath.Join(root, "manifests", "bodycam", "0.8.12.19193.json"))
	if after.BlockSize != BlockSize {
		t.Fatalf("blockSize = %d", after.BlockSize)
	}
	want := base64.StdEncoding.EncodeToString(referenceBlocks(big, BlockSize))
	if got := manifestEntry(t, after, "Paks/big.pak").Blocks; got != want {
		t.Fatalf("blocks = %q, want %q", got, want)
	}
	// Меняется только то, ради чего команда запущена: сборка та же, и лаунчер
	// не должен увидеть в ней новую версию.
	if after.BuildID != before.BuildID || after.CreatedAt != before.CreatedAt || after.Version != before.Version {
		t.Fatalf("identity changed: before %+v, after %+v", before, after)
	}
	for _, f := range before.Files {
		g := manifestEntry(t, after, f.Path)
		if g.Size != f.Size || g.Sha256 != f.Sha256 || g.Blake3 != f.Blake3 || g.Executable != f.Executable {
			t.Fatalf("%s changed: %+v → %+v", f.Path, f, g)
		}
	}
}

func TestBackfillIsIdempotent(t *testing.T) {
	root := t.TempDir()
	publishedBeforeBlocks(t, root, NamespaceGame, "g", "1.0.0", map[string][]byte{"a.pak": blockPattern(2 * BlockSize)})
	h := New(root)
	if _, err := h.BackfillBlocks(NamespaceGame, "g", "1.0.0"); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(root, "manifests", "g", "1.0.0.json")
	first, _ := os.ReadFile(path)
	res, err := h.BackfillBlocks(NamespaceGame, "g", "1.0.0")
	if err != nil || res.Skipped == "" {
		t.Fatalf("second run = %+v, %v; want skipped", res, err)
	}
	second, _ := os.ReadFile(path)
	if string(first) != string(second) {
		t.Fatal("a second run rewrote the manifest")
	}
}

// Файл на диске разошёлся с манифестом — значит, неизвестно, что раздаётся.
// Блоки по такому содержимому указали бы лаунчеру собирать не тот файл.
func TestBackfillRefusesWhenFilesDoNotMatchTheManifest(t *testing.T) {
	root := t.TempDir()
	publishedBeforeBlocks(t, root, NamespaceGame, "g", "1.0.0", map[string][]byte{"a.pak": blockPattern(2 * BlockSize)})
	path := filepath.Join(root, "manifests", "g", "1.0.0.json")
	orig, _ := os.ReadFile(path)

	tampered := blockPattern(2 * BlockSize)
	tampered[5] ^= 0xFF
	writeFile(t, filepath.Join(root, "content", "g", "1.0.0", "files", "a.pak"), tampered)

	if _, err := New(root).BackfillBlocks(NamespaceGame, "g", "1.0.0"); err == nil {
		t.Fatal("backfill accepted a file that differs from its manifest")
	}
	now, _ := os.ReadFile(path)
	if string(now) != string(orig) {
		t.Fatal("manifest was rewritten despite the mismatch")
	}
}

func TestBackfillRefusesMissingFiles(t *testing.T) {
	root := t.TempDir()
	publishedBeforeBlocks(t, root, NamespaceGame, "g", "1.0.0", map[string][]byte{"a.pak": blockPattern(2 * BlockSize)})
	if err := os.Remove(filepath.Join(root, "content", "g", "1.0.0", "files", "a.pak")); err != nil {
		t.Fatal(err)
	}
	if _, err := New(root).BackfillBlocks(NamespaceGame, "g", "1.0.0"); err == nil {
		t.Fatal("backfill succeeded without the file it had to hash")
	}
}

// Перепубликация той же версии, пришедшаяся на время подсчёта, не должна
// быть затёрта манифестом, по которому считали.
func TestBackfillDoesNotOverwriteARepublishedManifest(t *testing.T) {
	root := t.TempDir()
	publishedBeforeBlocks(t, root, NamespaceGame, "g", "1.0.0", map[string][]byte{"a.pak": blockPattern(2 * BlockSize)})
	path := filepath.Join(root, "manifests", "g", "1.0.0.json")
	republished := []byte(`{"version":"1.0.0","buildId":"build-2","gameId":"g","files":[],"emptyDirs":[]}`)

	orig := backfillHashed
	backfillHashed = func() { writeFile(t, path, republished) }
	defer func() { backfillHashed = orig }()

	_, err := New(root).BackfillBlocks(NamespaceGame, "g", "1.0.0")
	if !errors.Is(err, ErrManifestChanged) {
		t.Fatalf("err = %v, want ErrManifestChanged", err)
	}
	now, _ := os.ReadFile(path)
	if string(now) != string(republished) {
		t.Fatal("the republished manifest was overwritten")
	}
}

func TestBackfillCoversModpacksAndSkipsTheLauncher(t *testing.T) {
	root := t.TempDir()
	publishedBeforeBlocks(t, root, NamespaceGame, "peak", "2.4.3", map[string][]byte{"a.pak": blockPattern(2 * BlockSize)})
	publishedBeforeBlocks(t, root, NamespaceMods, "peak", "Team-Pack-1.0.0", map[string][]byte{"BepInEx/big.dll": blockPattern(2 * BlockSize)})
	publishedBeforeBlocks(t, root, NamespaceGame, "launcher", "1.6.63", map[string][]byte{"ChillHub.dll": blockPattern(2 * BlockSize)})
	h := New(root)

	targets, err := h.BackfillTargets("", "")
	if err != nil {
		t.Fatal(err)
	}
	got := map[string]bool{}
	for _, tg := range targets {
		got[string(tg.NS)+"|"+tg.GameID+"|"+tg.Version] = true
	}
	want := map[string]bool{"|peak|2.4.3": true, "_mods|peak|Team-Pack-1.0.0": true}
	if len(got) != len(want) {
		t.Fatalf("targets = %v, want %v", got, want)
	}
	for k := range want {
		if !got[k] {
			t.Fatalf("targets = %v, missing %s", got, k)
		}
	}

	if _, err := h.BackfillBlocks(NamespaceMods, "peak", "Team-Pack-1.0.0"); err != nil {
		t.Fatal(err)
	}
	m := readManifestFile(t, filepath.Join(root, "manifests", "_mods", "peak", "Team-Pack-1.0.0.json"))
	if manifestEntry(t, m, "BepInEx/big.dll").Blocks == "" {
		t.Fatal("modpack manifest got no blocks")
	}

	only, err := h.BackfillTargets("peak", "2.4.3")
	if err != nil || len(only) != 1 || only[0].NS != NamespaceGame {
		t.Fatalf("narrowed targets = %+v, %v", only, err)
	}
}

func TestBackfillRejectsUnsafeNames(t *testing.T) {
	h := New(t.TempDir())
	for _, tc := range [][2]string{{"../x", "1.0"}, {"g", "../../etc"}, {"", "1"}} {
		if _, err := h.BackfillBlocks(NamespaceGame, tc[0], tc[1]); err == nil {
			t.Errorf("BackfillBlocks(%q, %q) accepted", tc[0], tc[1])
		}
	}
}

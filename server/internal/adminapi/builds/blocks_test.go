package builds

import (
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// blockPattern — содержимое, в котором все блоки разные. Простая «пила»
// byte(i) повторяется каждые 256 байт, и все её блоки по мегабайту одинаковы:
// тест на порядок блоков на ней прошёл бы и с перепутанным порядком.
func blockPattern(n int) []byte {
	b := make([]byte, n)
	for i := range b {
		b[i] = byte(i*31 + i>>16)
	}
	return b
}

// referenceBlocks считает хеши блоков в лоб, по определению формата.
func referenceBlocks(data []byte, bs int) []byte {
	var out []byte
	for off := 0; off < len(data); off += bs {
		sum := sha256.Sum256(data[off:min(off+bs, len(data))])
		out = append(out, sum[:blockDigestBytes]...)
	}
	return out
}

func TestBlockHasherMatchesTheDefinitionForAnyWriteSizes(t *testing.T) {
	for _, size := range []int{0, 1, BlockSize - 1, BlockSize, BlockSize + 1, 3*BlockSize + 5} {
		data := blockPattern(size)
		want := referenceBlocks(data, BlockSize)
		// Файл приходит кусками произвольной длины: io.Copy режет его по своему
		// буферу, и граница блока почти никогда не совпадает с границей куска.
		for _, piece := range []int{1, 7, 4096, BlockSize - 3, BlockSize + 11} {
			if size > 64<<10 && piece < 4096 {
				continue // побайтово мегабайты гонять незачем
			}
			h := newBlockHasher(BlockSize)
			for off := 0; off < len(data); off += piece {
				_, _ = h.Write(data[off:min(off+piece, len(data))])
			}
			if got := h.digests(); string(got) != string(want) {
				t.Fatalf("size %d, pieces of %d: block digests differ from the definition", size, piece)
			}
		}
	}
}

func TestSingleBlockFilesGetNoBlockList(t *testing.T) {
	for _, size := range []int64{0, 1, BlockSize} {
		if got := encodeBlocks(make([]byte, blockDigestBytes), size); got != "" {
			t.Errorf("size %d: blocks = %q, want none — one block is the file itself", size, got)
		}
	}
	two := referenceBlocks(blockPattern(BlockSize+1), BlockSize)
	if got := encodeBlocks(two, BlockSize+1); got == "" {
		t.Fatal("a file of two blocks must get its block list")
	}
}

// Двенадцать байт на блок ложатся в base64 ровно шестнадцатью символами, без
// выравнивания. На этом стоит разбор у лаунчера: строка режется по 16 символов.
func TestBlockDigestIsSixteenBase64Chars(t *testing.T) {
	enc := encodeBlocks(referenceBlocks(blockPattern(3*BlockSize), BlockSize), 3*BlockSize)
	if len(enc) != 3*16 || strings.Contains(enc, "=") {
		t.Fatalf("3 blocks encoded as %d chars (%q), want 48 without padding", len(enc), enc)
	}
}

// CROSS-LANGUAGE BLOCK CONTRACT.
//
// Сервер пишет хеши блоков, лаунчер считает их для своей старой копии файла и
// ищет совпадения. Разойдись две реализации хоть в чём-то — в обрезке, в
// порядке, в последнем неполном блоке, — ошибкой это не станет нигде: просто
// ни один блок не совпадёт, и каждое обновление снова будет качать файлы
// целиком. Та же строка закреплена в launcher/tests/ChillHub.Tests/BlockDeltaTests.cs.
func TestBlockDigestsMatchTheLauncherImplementation(t *testing.T) {
	const want = "H39kQAX/MP6y/vPk1TW5uQorQJYe+vO52f4Zuf3Q3yhS4Lq0"
	data := blockPattern(2*BlockSize + BlockSize/2)
	h := newBlockHasher(BlockSize)
	_, _ = h.Write(data)
	if got := encodeBlocks(h.digests(), int64(len(data))); got != want {
		t.Fatalf("blocks(2.5 MiB pattern) = %s, want %s\n"+
			"The server and the launcher no longer agree on block hashes: every "+
			"update would download whole files again.", got, want)
	}
}

func TestPublishedManifestCarriesBlocksForLargeFilesOnly(t *testing.T) {
	root := t.TempDir()
	big := blockPattern(2*BlockSize + 17)
	writeFile(t, filepath.Join(root, "Paks", "big.pak"), big)
	writeFile(t, filepath.Join(root, "small.ini"), []byte("x=1"))
	writeFile(t, filepath.Join(root, "one-block.bin"), blockPattern(BlockSize))

	files, dirs, err := scanManifest(root)
	if err != nil {
		t.Fatal(err)
	}
	m, err := prepareManifest(manifest{GameID: "g", Version: "1", Files: files, EmptyDirs: dirs})
	if err != nil {
		t.Fatalf("a freshly composed manifest must be publishable: %v", err)
	}
	if m.BlockSize != BlockSize {
		t.Fatalf("blockSize = %d, want %d", m.BlockSize, BlockSize)
	}
	want := base64.StdEncoding.EncodeToString(referenceBlocks(big, BlockSize))
	if got := manifestEntry(t, m, "Paks/big.pak").Blocks; got != want {
		t.Fatalf("big.pak blocks = %q, want %q", got, want)
	}
	for _, p := range []string{"small.ini", "one-block.bin"} {
		if b := manifestEntry(t, m, p).Blocks; b != "" {
			t.Errorf("%s got blocks %q; a single-block file needs none", p, b)
		}
	}
}

// Манифест без единого большого файла остаётся ровно таким, каким был до
// блоков: ни пустого "blocks", ни "blockSize" — старым лаунчерам и глазу
// оператора в нём нечего нового видеть.
func TestManifestWithoutLargeFilesHasNoBlockFields(t *testing.T) {
	root := t.TempDir()
	writeFile(t, filepath.Join(root, "a.txt"), []byte("hello"))
	files, dirs, err := scanManifest(root)
	if err != nil {
		t.Fatal(err)
	}
	m, err := prepareManifest(manifest{GameID: "g", Version: "1", Files: files, EmptyDirs: dirs})
	if err != nil {
		t.Fatal(err)
	}
	b, err := json.Marshal(m)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(b), "blockSize") || strings.Contains(string(b), "blocks") {
		t.Fatalf("manifest of small files mentions blocks: %s", b)
	}
}

func TestInconsistentBlocksAreRejected(t *testing.T) {
	good := base64.StdEncoding.EncodeToString(referenceBlocks(blockPattern(2*BlockSize), BlockSize))
	for name, tc := range map[string]struct {
		bs     int64
		size   int64
		blocks string
	}{
		"no block size":        {0, 2 * BlockSize, good},
		"block size too small": {1024, 2 * BlockSize, good},
		"block size too large": {1 << 30, 2 * BlockSize, good},
		"not base64":           {BlockSize, 2 * BlockSize, "!!!!"},
		"one digest short":     {BlockSize, 3 * BlockSize, good},
		"one digest too many":  {BlockSize, BlockSize + 1, good + good[:16]},
	} {
		m := manifest{BlockSize: tc.bs, Files: []manifestFile{{Path: "a.pak", Size: tc.size, Sha256: "x", Blocks: tc.blocks}}}
		if err := validateManifest(m); err == nil {
			t.Errorf("%s: manifest accepted, want rejection", name)
		}
	}
	ok := manifest{BlockSize: BlockSize, Files: []manifestFile{{Path: "a.pak", Size: 2 * BlockSize, Sha256: "x", Blocks: good}}}
	if err := validateManifest(ok); err != nil {
		t.Fatalf("consistent blocks rejected: %v", err)
	}
}

func writeFile(t *testing.T, path string, data []byte) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
}

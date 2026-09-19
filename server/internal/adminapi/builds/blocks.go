package builds

import (
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"hash"
	"strconv"
)

// Хеши блоков — то, что позволяет лаунчеру качать при обновлении не файл
// целиком, а только изменившиеся его куски.
//
// Сравнение по хешу всего файла здесь бессильно. Игры на Unreal Engine держат
// данные в pak-архивах по гигабайту и больше, и правка в индексе архива делает
// другим весь файл. Замер на Bodycam 1.0.0 → 0.8.12.19193: файлы, у которых не
// сошёлся хеш, весят 52.9 ГБ, а новых блоков по 1 МиБ в них — 2.6 ГБ.
// Остальное лаунчер уже держит на диске, в старой копии того же файла.
//
// Формат: блок — BlockSize байт подряд с начала НОВОГО файла, последний может
// быть короче. На каждый блок — blockDigestBytes байт: скользящий хеш (8 байт,
// little-endian, см. rollingHash) и первые 8 байт его SHA-256. Хеши блоков
// файла склеены по порядку и записаны одной base64-строкой в поле "blocks";
// размер блока объявлен один раз на манифест, в "blockSize".
//
// Скользящий хеш — то, что находит блок в старой копии на ЛЮБОМ смещении, а не
// только кратном блоку (схема zsync). Лаунчер катит окно размером в блок по
// своему старому файлу с шагом в байт: сдвинуть хеш на байт стоит одно
// умножение, и полный SHA-256 считается только там, где совпал скользящий.
// Без этого правка, вставившая в файл сотню байт, сдвигала весь хвост мимо
// границ блоков, и ни один блок после неё не совпадал. Замер на PEAK 2.4.1 →
// 2.4.3 (Unity, файлы подросли на сотни байт): по выровненным блокам 4.25 ГБ
// из 4.28, скользящим окном — 2.49; на Bodycam 2.64 и 2.61 соответственно.
//
// Хеш блока — подсказка, а не гарантия. Лаунчер собирает файл из совпавших
// блоков и докачанных отрезков, но принимает результат только по полному
// SHA-256 и Blake3 файла, как и раньше. Поэтому хватает восьми байт SHA-256:
// случайное совпадение (одновременно со скользящим хешем) стоило бы одной
// лишней загрузки файла, а не подмены.
//
// SHA-256, а не Blake3: его посчитает любая машина игрока, а Blake3 у лаунчера
// бывает недоступен (неполная установка), и тогда блочная загрузка молча
// превратилась бы в полную.
//
// Файлу из одного блока хеши не пишутся: его единственный блок — это он сам,
// и сравнить его по частям не выйдет.
const (
	// BlockSize — размер блока, который сервер пишет в новые манифесты.
	// 1 МиБ, как у Steam: мельче — длиннее список и больше запросов на
	// разрозненные правки, крупнее — больше лишних байт вокруг каждой правки.
	BlockSize = 1 << 20

	// blockDigestBytes — сколько байт на блок в манифесте: скользящий хеш и
	// начало SHA-256, по восемь байт.
	blockDigestBytes = 16

	// rollingPrime — множитель скользящего хеша. Нечётный (иначе младшие биты
	// вырождаются), с далеко разнесёнными единицами, чтобы байт размазывался
	// по всему слову. Тот же, что у лаунчера (BlockList.RollingPrime).
	rollingPrime uint64 = 0x100000001B3

	// Пределы размера блока, который манифест вправе объявить. Лаунчер держит
	// те же: блок в байт или гигабайт — не формат, а поломка.
	minBlockSize = 64 << 10
	maxBlockSize = 64 << 20
)

// blockHasher считает хеши блоков потоком, в том же проходе по файлу, что
// и полные хеши: читать многогигабайтный файл второй раз ради них незачем.
type blockHasher struct {
	size   int
	cur    hash.Hash
	weak   uint64
	filled int
	out    []byte
}

func newBlockHasher(size int) *blockHasher {
	return &blockHasher{size: size, cur: sha256.New()}
}

// Write режет поток на блоки. Ошибок не бывает: это счётчик, а не запись.
func (b *blockHasher) Write(p []byte) (int, error) {
	n := len(p)
	for len(p) > 0 {
		take := min(len(p), b.size-b.filled)
		_, _ = b.cur.Write(p[:take])
		b.weak = rollingHash(b.weak, p[:take])
		b.filled += take
		p = p[take:]
		if b.filled == b.size {
			b.finishBlock()
		}
	}
	return n, nil
}

func (b *blockHasher) finishBlock() {
	b.out = binary.LittleEndian.AppendUint64(b.out, b.weak)
	sum := b.cur.Sum(nil)
	b.out = append(b.out, sum[:8]...)
	b.cur.Reset()
	b.weak = 0
	b.filled = 0
}

// rollingHash продолжает скользящий хеш h байтами p: h·P^len(p) + Σ p[i]·P^(len-1-i)
// по модулю 2^64. Хеш блока — rollingHash(0, блок). Лаунчер сдвигает окно на
// байт как h' = (h − вышедший·P^(W−1))·P + вошедший, и это то же число, что
// посчитанное здесь с нуля для нового окна.
func rollingHash(h uint64, p []byte) uint64 {
	for _, c := range p {
		h = h*rollingPrime + uint64(c)
	}
	return h
}

// digests закрывает последний, неполный блок и возвращает хеши всех блоков.
// Зовётся один раз, после того как файл прочитан целиком.
func (b *blockHasher) digests() []byte {
	if b.filled > 0 {
		b.finishBlock()
	}
	return b.out
}

// blockCount — сколько блоков размера bs занимает файл размера size.
func blockCount(size, bs int64) int64 {
	if size <= 0 || bs <= 0 {
		return 0
	}
	return (size + bs - 1) / bs
}

// encodeBlocks превращает хеши блоков файла размера size в значение поля
// "blocks". Пусто для файлов, которым хеши блоков не нужны.
func encodeBlocks(digests []byte, size int64) string {
	if blockCount(size, BlockSize) < 2 {
		return ""
	}
	return base64.StdEncoding.EncodeToString(digests)
}

// blocksProblem объясняет, чем поле "blocks" файла не годится при размере
// блока bs, или возвращает пустую строку.
//
// Манифест с несогласованными блоками опубликовать нельзя: лаунчер такие блоки
// отбросит и качать будет файлом целиком, то есть ровно то, ради чего их пишут,
// пропадёт — и пропадёт молча.
func blocksProblem(f manifestFile, bs int64) string {
	if f.Blocks == "" {
		return ""
	}
	if bs < minBlockSize || bs > maxBlockSize {
		return "blockSize " + strconv.FormatInt(bs, 10) + " is out of range"
	}
	raw, err := base64.StdEncoding.DecodeString(f.Blocks)
	if err != nil {
		return "blocks is not base64"
	}
	want := blockCount(f.Size, bs)
	if want < 2 {
		// Единственный блок — это сам файл: лаунчер такой список не использует.
		return "a file of one block needs no blocks"
	}
	if int64(len(raw)) != want*blockDigestBytes {
		return "blocks lists " + strconv.Itoa(len(raw)/blockDigestBytes) + " digests, size needs " + strconv.FormatInt(want, 10)
	}
	return ""
}

// withoutBlocks возвращает записи без хешей блоков, не трогая исходный срез.
func withoutBlocks(files []manifestFile) []manifestFile {
	if !hasBlocks(files) {
		return files
	}
	out := make([]manifestFile, len(files))
	copy(out, files)
	for i := range out {
		out[i].Blocks = ""
	}
	return out
}

// hasBlocks — есть ли хоть у одного файла хеши блоков.
func hasBlocks(files []manifestFile) bool {
	for _, f := range files {
		if f.Blocks != "" {
			return true
		}
	}
	return false
}

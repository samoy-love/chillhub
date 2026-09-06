package builds

import (
	"archive/zip"
	"bytes"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"

	"ChillHub/server/internal/adminutil"
)

// РАСПАКОВКА И ХЕШИРОВАНИЕ ИДУТ В НЕСКОЛЬКО ПОТОКОВ, И ЭТО ВИДНО СНАРУЖИ
// ТРЕМЯ СПОСОБАМИ — каждый из них ломается молча, поэтому закреплён здесь.
//
// Первое: манифест обязан остаться прежним. Он уезжает игрокам и по нему
// считается отпечаток сборки; переставленные строки — это другой файл при том
// же содержимом, то есть лишний повод считать сборку изменившейся и позвать
// всех обновляться впустую.
//
// Второе: поток событий обязан идти по порядку. Оператор смотрит на него
// минутами, и прогресс, скачущий взад-вперёд, читается как сбой.
//
// Третье: защита от zip-бомбы обязана держать общий лимит, а не лимит на
// поток. Раньше остаток байт был обычным полем: каждый поток читал бы его до
// вычитаний соседей, и вместе они выписали бы на диск больше разрешённого,
// каждый «в пределах».

// manyFilesZip собирает архив из n файлов с разным содержимым.
func manyFilesZip(t *testing.T, n int) []byte {
	t.Helper()
	var buf bytes.Buffer
	z := zip.NewWriter(&buf)
	for i := range n {
		w, err := z.Create(fmt.Sprintf("files/dir%02d/file%03d.txt", i%7, i))
		if err != nil {
			t.Fatal(err)
		}
		if _, err := w.Write([]byte(strings.Repeat(fmt.Sprintf("%d ", i), 40))); err != nil {
			t.Fatal(err)
		}
	}
	if err := z.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func TestManifestOrderDoesNotDependOnWorkers(t *testing.T) {
	// Порядок задаёт обход каталога, а не то, какой поток успел первым.
	root := t.TempDir()
	files := filepath.Join(root, "files")
	if err := os.MkdirAll(files, 0o755); err != nil {
		t.Fatal(err)
	}
	unzipTestArchive(t, manyFilesZip(t, 200), files)

	first, _, err := walkManifest(files, nil)
	if err != nil {
		t.Fatal(err)
	}
	for range 5 {
		again, _, err := walkManifest(files, nil)
		if err != nil {
			t.Fatal(err)
		}
		if len(again) != len(first) {
			t.Fatalf("длина манифеста поплыла: %d против %d", len(again), len(first))
		}
		for i := range first {
			if again[i] != first[i] {
				t.Fatalf("строка %d разъехалась между прогонами:\n%+v\n%+v", i, first[i], again[i])
			}
		}
	}
}

func TestManifestProgressArrivesInOrder(t *testing.T) {
	root := t.TempDir()
	files := filepath.Join(root, "files")
	if err := os.MkdirAll(files, 0o755); err != nil {
		t.Fatal(err)
	}
	unzipTestArchive(t, manyFilesZip(t, 150), files)

	var seen []string
	all, _, err := walkManifest(files, func(mf manifestFile) {
		seen = append(seen, mf.Path)
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(seen) != len(all) {
		t.Fatalf("событий %d, файлов в манифесте %d", len(seen), len(all))
	}
	for i := range all {
		if seen[i] != all[i].Path {
			t.Fatalf("событие %d не совпало с манифестом: %q против %q", i, seen[i], all[i].Path)
		}
	}
}

func TestFailedFileStopsTheWholeWalk(t *testing.T) {
	// Один сорвавшийся файл обязан валить проход целиком: манифест без строки
	// — это сборка, у которой файл молча пропал. И сообщать наружу о том, что
	// идёт ПОСЛЕ отказа, тоже нельзя: оператор увидел бы прогресс у сборки,
	// которая уже не соберётся.
	boom := errors.New("файл не прочитался")
	var reported []int

	err := hashAll(50, func(i int) error {
		if i == 7 {
			return boom
		}
		return nil
	}, func(i int) {
		reported = append(reported, i)
	})

	if !errors.Is(err, boom) {
		t.Fatalf("проход вернул %v, а не отказ седьмого файла", err)
	}
	for _, i := range reported {
		if i >= 7 {
			t.Fatalf("наружу ушёл файл %d — он идёт после отказавшего седьмого", i)
		}
	}
}

func TestEveryFileIsProcessedExactlyOnce(t *testing.T) {
	// Потоков много, а работа у каждого файла одна: посчитанный дважды файл
	// — это лишняя строка в манифесте, пропущенный — недостающая.
	const n = 500
	var mu sync.Mutex
	seen := make([]int, n)

	if err := hashAll(n, func(i int) error {
		mu.Lock()
		seen[i]++
		mu.Unlock()
		return nil
	}, func(int) {}); err != nil {
		t.Fatal(err)
	}

	for i, c := range seen {
		if c != 1 {
			t.Fatalf("файл %d обработан %d раз", i, c)
		}
	}
}

func TestExtractBudgetHoldsUnderConcurrency(t *testing.T) {
	// Общий лимит, а не лимит на поток: восемь потоков по одному килобайту
	// не имеют права выписать четыре, когда разрешено два.
	const limit = 2048
	b := adminutil.NewExtractBudget(limit)

	var wrote int64
	errs := 0
	done := make(chan struct{})
	for range 8 {
		go func() {
			defer func() { done <- struct{}{} }()
			var sink countingWriter
			err := b.Copy(&sink, strings.NewReader(strings.Repeat("x", 1024)))
			if err != nil {
				errs++
				return
			}
			wrote += sink.n
		}()
	}
	for range 8 {
		<-done
	}

	if wrote > limit {
		t.Fatalf("на диск ушло %d байт при лимите %d — лимит оказался на поток, а не общий", wrote, limit)
	}
	if errs == 0 {
		t.Fatal("ни один поток не получил отказа, хотя суммарно они просили вчетверо больше лимита")
	}
}

type countingWriter struct{ n int64 }

func (c *countingWriter) Write(p []byte) (int, error) {
	c.n += int64(len(p))
	return len(p), nil
}

// unzipTestArchive раскладывает архив на диск тем же кодом, что и публикация.
func unzipTestArchive(t *testing.T, data []byte, target string) {
	t.Helper()
	zipPath := filepath.Join(t.TempDir(), "a.zip")
	if err := os.WriteFile(zipPath, data, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := unzipTo(zipPath, target); err != nil {
		t.Fatal(err)
	}
}

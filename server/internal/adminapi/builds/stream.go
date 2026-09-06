package builds

import (
	"archive/zip"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"sync"

	"ChillHub/server/internal/adminutil"

	"github.com/zeebo/blake3"
)

// The two NDJSON publish pipelines (POST /uploadStream and the chunked
// /upload/process) emit the same event stream, so extraction and manifest
// composition live here instead of being duplicated per handler.

// emitEventf writes one NDJSON event.
//
// The write error is dropped, deliberately and in exactly this one place: the
// only way it fails is a client that hung up, and the publication must still
// run to completion — abandoning it half-way would leave a staging directory
// and, worse, a build the operator believes is live. There is also nowhere left
// to report it to, the response being the stream itself.
func emitEventf(w io.Writer, format string, a ...any) {
	_, _ = fmt.Fprintf(w, format, a...)
}

// streamUnzip extracts zipPath into filesRoot, emitting one {"type":"unzip"}
// event per entry. On failure it emits {"type":"error"} and reports false.
func streamUnzip(w io.Writer, fl adminutil.Flusher, zipPath, filesRoot string) bool {
	zr, err := zip.OpenReader(zipPath)
	if err != nil {
		streamError(w, fl, err.Error())
		return false
	}
	defer func() { _ = zr.Close() }()
	// See extractBudget: the entry sizes in the archive cannot be trusted, so
	// the bytes actually written are counted and capped.
	budget := newExtractBudget()

	// Проверки пути — до единой записи на диск и по порядку: выход за
	// пределы каталога обязан останавливать распаковку целиком, а не после
	// того, как соседний поток уже что-то создал.
	type job struct {
		zf   *zip.File
		rel  string
		full string
		dir  bool
	}
	jobs := make([]job, 0, len(zr.File))
	for _, zf := range zr.File {
		rel := zipEntryRelPath(zf)
		if rel == "" {
			continue
		}
		full := filepath.Join(filesRoot, rel)
		if !adminutil.EnsureWithin(filesRoot, full) {
			streamError(w, fl, errZipSlip.Error()+": "+rel)
			return false
		}
		jobs = append(jobs, job{zf: zf, rel: rel, full: full,
			dir: zf.FileInfo().IsDir() || hasTrailingSlash(rel)})
	}

	// Каталоги создаём заранее и последовательно: иначе десяток потоков
	// делают MkdirAll на один и тот же путь.
	for _, j := range jobs {
		if j.dir {
			_ = os.MkdirAll(j.full, contentDirPerm)
			continue
		}
		if err := makeEntryParent(j.full); err != nil {
			streamError(w, fl, err.Error())
			return false
		}
	}

	// РАСПАКОВКА ШЛА В ОДИН ПОТОК, А ЭТО САМЫЙ ДОЛГИЙ ШАГ ПУБЛИКАЦИИ.
	// Сборка в полтора гигабайта распаковывалась четыре секунды из шести
	// на тридцатидвухъядерной машине; на четырёхъядерном arm64 прода тот
	// же проход стоит кратно дороже. Работа здесь и дисковая, и
	// процессорная (распаковка), и та и другая умеют идти параллельно.
	//
	// Порядок событий держится тем же приёмом, что у хеширования: готовая
	// запись ждёт своей очереди, и оператор видит ровный список.
	if err := hashAll(len(jobs), func(i int) error {
		if jobs[i].dir {
			return nil
		}
		return extractZipEntry(jobs[i].zf, jobs[i].rel, jobs[i].full, budget)
	}, func(i int) {
		emitEventf(w, "{\"type\":\"unzip\",\"path\":%q}\n", jobs[i].rel)
		fl.Flush()
	}); err != nil {
		streamError(w, fl, err.Error())
		return false
	}
	return true
}

// streamCompose walks filesRoot, hashing every file and emitting one
// {"type":"file"} event per entry. On failure it emits {"type":"error"} and
// reports false.
func streamCompose(w io.Writer, fl adminutil.Flusher, filesRoot string) ([]manifestFile, []string, bool) {
	var idx int
	var bytesDone int64
	files, emptyDirs, err := walkManifest(filesRoot, func(mf manifestFile) {
		idx++
		bytesDone += mf.Size
		emitEventf(w, "{\"type\":\"file\",\"idx\":%d,\"path\":%q,\"bytesDone\":%d}\n", idx, mf.Path, bytesDone)
		fl.Flush()
	})
	if err != nil {
		streamError(w, fl, err.Error())
		return nil, nil, false
	}
	return files, emptyDirs, true
}

// scanManifest walks filesRoot without emitting progress; used by the
// non-streaming upload endpoint.
func scanManifest(filesRoot string) ([]manifestFile, []string, error) {
	return walkManifest(filesRoot, nil)
}

// walkManifest hashes every file under filesRoot and returns the manifest
// entries plus the directories that hold no files. onFile, when set, is called
// after each entry, which is how the streaming publish paths report progress.
//
// The two publish pipelines used to carry a copy of this walk each; they drifted
// apart once already (only one of them stopped ignoring a failed d.Info(), the
// other kept panicking on the nil FileInfo).
func walkManifest(filesRoot string, onFile func(manifestFile)) ([]manifestFile, []string, error) {
	dirHasFile := map[string]bool{}
	allDirs := map[string]bool{}

	// Сначала обход без единого хеша: он дешёвый и задаёт ПОРЯДОК.
	type entry struct {
		path string
		rel  string
		size int64
	}
	var list []entry
	err := filepath.WalkDir(filesRoot, func(path string, d os.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, _ := filepath.Rel(filesRoot, path)
		rel = filepath.ToSlash(rel)
		if d.IsDir() {
			allDirs[rel] = true
			return nil
		}
		if rel == "." {
			return nil
		}
		// A missing FileInfo used to be ignored and info.Size() then panicked on
		// nil. The manifest cannot be built without the size, so fail the walk.
		info, err := d.Info()
		if err != nil {
			return err
		}
		list = append(list, entry{path: path, rel: rel, size: info.Size()})
		markParentDirs(dirHasFile, rel)
		return nil
	})
	if err != nil {
		return nil, nil, err
	}

	files := make([]manifestFile, len(list))
	if err := hashAll(len(list), func(i int) error {
		b3Sum, shaSum, herr := hashFile(list[i].path)
		if herr != nil {
			return herr
		}
		files[i] = manifestFile{
			Path:       list[i].rel,
			Size:       list[i].size,
			Blake3:     b3Sum,
			Sha256:     shaSum,
			Executable: isExecutable(list[i].rel),
		}
		return nil
	}, func(i int) {
		if onFile != nil {
			onFile(files[i])
		}
	}); err != nil {
		return nil, nil, err
	}

	return files, emptyDirsOf(allDirs, dirHasFile), nil
}

// hashWorkers — сколько файлов хешируется одновременно.
//
// ХЕШИРОВАНИЕ БЫЛО ОДНОПОТОЧНЫМ, А СЧИТАЕТСЯ ДВА ХЕША НА КАЖДЫЙ ФАЙЛ.
// Сборка в полтора гигабайта и шесть тысяч файлов проводила в этом месте
// две секунды на машине, где сборка занимает шесть, — и это машина с
// тридцатью двумя ядрами. Прод — четырёхъядерный arm64 без ускорителей
// sha256, там тот же проход стоит кратно дороже, и растёт он вместе с
// размером сборки, то есть ровно тогда, когда ждать больнее всего.
//
// Потолок в восемь потоков, а не «сколько ядер»: работа упирается и в
// диск тоже, а от разгона очереди чтений на HDD становится хуже, чем
// лучше. Восемь загружают и четыре ядра прода, и не превращают чтение в
// случайное.
func hashWorkers() int {
	return max(min(runtime.GOMAXPROCS(0), 8), 1)
}

// hashAll считает n задач параллельно, а сообщает о них ПО ПОРЯДКУ.
//
// Порядок здесь не украшение. В том же порядке файлы попадают в манифест,
// и он уезжает игрокам; перемешанный манифест — это другой файл при том же
// содержимом, то есть лишний повод считать сборку изменившейся. И тот же
// порядок видит оператор в потоке событий: прогресс, скачущий взад-вперёд,
// читается как сбой, а не как ускорение.
//
// Поэтому готовые задачи ждут своей очереди: как только готов очередной
// префикс, он и уходит наружу.
func hashAll(n int, work func(int) error, report func(int)) error {
	if n == 0 {
		return nil
	}

	done := make([]bool, n)
	var mu sync.Mutex
	var next int
	var firstErr error

	jobs := make(chan int)
	var wg sync.WaitGroup
	for range hashWorkers() {
		wg.Go(func() {
			for i := range jobs {
				err := work(i)

				mu.Lock()
				if err != nil && firstErr == nil {
					firstErr = err
				}
				done[i] = true
				for next < n && done[next] {
					if firstErr == nil {
						report(next)
					}
					next++
				}
				mu.Unlock()
			}
		})
	}

	for i := range n {
		mu.Lock()
		stop := firstErr != nil
		mu.Unlock()
		if stop {
			break
		}
		jobs <- i
	}
	close(jobs)
	wg.Wait()

	return firstErr
}

// hashFile returns the blake3 and sha256 digests of one extracted file.
func hashFile(path string) (string, string, error) {
	f, err := os.Open(path)
	if err != nil {
		return "", "", err
	}
	defer func() { _ = f.Close() }()
	hSha := sha256.New()
	hB3 := blake3.New()
	if _, err := io.Copy(io.MultiWriter(hSha, hB3), f); err != nil {
		return "", "", err
	}
	return hex.EncodeToString(hB3.Sum(nil)), hex.EncodeToString(hSha.Sum(nil)), nil
}

// markParentDirs records every ancestor directory of rel as non-empty.
func markParentDirs(dirHasFile map[string]bool, rel string) {
	p := filepath.ToSlash(filepath.Dir(rel))
	for p != "." && p != "/" {
		dirHasFile[p] = true
		p = filepath.ToSlash(filepath.Dir(p))
	}
}

// emptyDirsOf returns the sorted, slash-terminated directories that hold no files.
func emptyDirsOf(allDirs, dirHasFile map[string]bool) []string {
	emptyDirs := make([]string, 0)
	for d := range allDirs {
		if d == "." || d == "" {
			continue
		}
		if !dirHasFile[d] {
			emptyDirs = append(emptyDirs, ensureTrailingSlash(d))
		}
	}
	sort.Strings(emptyDirs)
	return emptyDirs
}

func streamError(w io.Writer, fl adminutil.Flusher, msg string) {
	emitEventf(w, "{\"type\":\"error\",\"message\":%q}\n", msg)
	fl.Flush()
}

// ndjsonWriter wraps the ResponseWriter of a publish handler and remembers
// whether any event has already been sent.
//
// Once the first NDJSON line is out the status code is fixed at 200 and the
// body is a typed event stream: calling http.Error at that point is silently
// ignored except for the plain-text line it injects into the stream, which the
// client parses as garbage and — having seen no {"type":"error"} — reports the
// publication as successful. fail() therefore picks the only reporting channel
// that still works.
type ndjsonWriter struct {
	w       http.ResponseWriter
	fl      adminutil.Flusher
	started bool
}

func newNDJSONWriter(w http.ResponseWriter) *ndjsonWriter {
	return &ndjsonWriter{w: w, fl: adminutil.FlusherFor(w)}
}

func (n *ndjsonWriter) Write(p []byte) (int, error) {
	n.started = true
	return n.w.Write(p)
}

func (n *ndjsonWriter) Flush() { n.fl.Flush() }

// fail reports an error to the client: with an HTTP status while the response
// is still empty, as an error event once the stream has started.
func (n *ndjsonWriter) fail(code int, msg string) {
	if n.started {
		streamError(n, n, msg)
		return
	}
	http.Error(n.w, msg, code)
}

func hasTrailingSlash(s string) bool { return len(s) > 0 && s[len(s)-1] == '/' }

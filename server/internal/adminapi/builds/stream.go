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
	"sort"

	"ChillHub/server/internal/adminutil"

	"github.com/zeebo/blake3"
)

// The chunked NDJSON publish pipeline (/upload/process) emits its event
// stream through the helpers in this file, kept separate from the handler
// itself so extraction and manifest composition aren't duplicated inline.

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
		if zf.FileInfo().IsDir() || hasTrailingSlash(rel) {
			_ = os.MkdirAll(full, contentDirPerm)
			emitEventf(w, "{\"type\":\"unzip\",\"path\":%q}\n", rel)
			fl.Flush()
			continue
		}
		if err := extractZipEntry(zf, rel, full, budget); err != nil {
			streamError(w, fl, err.Error())
			return false
		}
		emitEventf(w, "{\"type\":\"unzip\",\"path\":%q}\n", rel)
		fl.Flush()
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
	var files []manifestFile
	dirHasFile := map[string]bool{}
	allDirs := map[string]bool{}
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
		b3Sum, shaSum, err := hashFile(path)
		if err != nil {
			return err
		}
		mf := manifestFile{
			Path:       rel,
			Size:       info.Size(),
			Blake3:     b3Sum,
			Sha256:     shaSum,
			Executable: isExecutable(rel),
		}
		files = append(files, mf)
		markParentDirs(dirHasFile, rel)
		if onFile != nil {
			onFile(mf)
		}
		return nil
	})
	if err != nil {
		return nil, nil, err
	}
	return files, emptyDirsOf(allDirs, dirHasFile), nil
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

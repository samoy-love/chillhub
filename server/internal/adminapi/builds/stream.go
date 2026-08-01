package builds

import (
	"archive/zip"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"

	"ChillHub/server/internal/adminutil"

	"github.com/zeebo/blake3"
)

// The two NDJSON publish pipelines (POST /uploadStream and the chunked
// /upload/process) emit the same event stream, so extraction and manifest
// composition live here instead of being duplicated per handler.

// streamUnzip extracts zipPath into filesRoot, emitting one {"type":"unzip"}
// event per entry. On failure it emits {"type":"error"} and reports false.
func streamUnzip(w io.Writer, fl adminutil.Flusher, zipPath, filesRoot string) bool {
	zr, err := zip.OpenReader(zipPath)
	if err != nil {
		streamError(w, fl, err.Error())
		return false
	}
	defer zr.Close()
	for _, zf := range zr.File {
		rel := zipEntryRelPath(zf)
		if rel == "" {
			continue
		}
		full := filepath.Join(filesRoot, rel)
		if !adminutil.EnsureWithin(filesRoot, full) {
			streamError(w, fl, "zip entry outside target: "+rel)
			return false
		}
		if zf.FileInfo().IsDir() || hasTrailingSlash(rel) {
			_ = os.MkdirAll(full, 0o755)
			fmt.Fprintf(w, "{\"type\":\"unzip\",\"path\":%q}\n", rel)
			fl.Flush()
			continue
		}
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			streamError(w, fl, err.Error())
			return false
		}
		rc, err := zf.Open()
		if err != nil {
			streamError(w, fl, err.Error())
			return false
		}
		out, err := os.OpenFile(full, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o644)
		if err != nil {
			rc.Close()
			streamError(w, fl, err.Error())
			return false
		}
		if _, err := io.Copy(out, rc); err != nil {
			out.Close()
			rc.Close()
			streamError(w, fl, err.Error())
			return false
		}
		out.Close()
		rc.Close()
		fmt.Fprintf(w, "{\"type\":\"unzip\",\"path\":%q}\n", rel)
		fl.Flush()
	}
	return true
}

// streamCompose walks filesRoot, hashing every file and emitting one
// {"type":"file"} event per entry. On failure it emits {"type":"error"} and
// reports false.
func streamCompose(w io.Writer, fl adminutil.Flusher, filesRoot string) ([]manifestFile, []string, bool) {
	var files []manifestFile
	dirHasFile := map[string]bool{}
	allDirs := map[string]bool{}
	var idx int
	var bytesDone int64
	errWalk := filepath.WalkDir(filesRoot, func(path string, d os.DirEntry, err error) error {
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
		info, _ := d.Info()
		f, err := os.Open(path)
		if err != nil {
			return err
		}
		hSha := sha256.New()
		hB3 := blake3.New()
		if _, err := io.Copy(io.MultiWriter(hSha, hB3), f); err != nil {
			f.Close()
			return err
		}
		f.Close()
		files = append(files, manifestFile{
			Path:       rel,
			Size:       info.Size(),
			Blake3:     hex.EncodeToString(hB3.Sum(nil)),
			Sha256:     hex.EncodeToString(hSha.Sum(nil)),
			Executable: isExecutable(rel),
		})
		p := filepath.ToSlash(filepath.Dir(rel))
		for p != "." && p != "/" {
			dirHasFile[p] = true
			p = filepath.ToSlash(filepath.Dir(p))
		}
		idx++
		bytesDone += info.Size()
		fmt.Fprintf(w, "{\"type\":\"file\",\"idx\":%d,\"path\":%q,\"bytesDone\":%d}\n", idx, rel, bytesDone)
		fl.Flush()
		return nil
	})
	if errWalk != nil {
		streamError(w, fl, errWalk.Error())
		return nil, nil, false
	}
	return files, emptyDirsOf(allDirs, dirHasFile), true
}

// scanManifest walks filesRoot without emitting progress; used by the
// non-streaming upload endpoint.
func scanManifest(filesRoot string) ([]manifestFile, []string, error) {
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
		info, _ := d.Info()
		f, err := os.Open(path)
		if err != nil {
			return err
		}
		hSha := sha256.New()
		hB3 := blake3.New()
		if _, err := io.Copy(io.MultiWriter(hSha, hB3), f); err != nil {
			f.Close()
			return err
		}
		f.Close()
		files = append(files, manifestFile{
			Path:       rel,
			Size:       info.Size(),
			Blake3:     hex.EncodeToString(hB3.Sum(nil)),
			Sha256:     hex.EncodeToString(hSha.Sum(nil)),
			Executable: isExecutable(rel),
		})
		p := filepath.ToSlash(filepath.Dir(rel))
		for p != "." && p != "/" {
			dirHasFile[p] = true
			p = filepath.ToSlash(filepath.Dir(p))
		}
		return nil
	})
	if err != nil {
		return nil, nil, err
	}
	return files, emptyDirsOf(allDirs, dirHasFile), nil
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
	fmt.Fprintf(w, "{\"type\":\"error\",\"message\":%q}\n", msg)
	fl.Flush()
}

func hasTrailingSlash(s string) bool { return len(s) > 0 && s[len(s)-1] == '/' }

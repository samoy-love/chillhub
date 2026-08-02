package builds

import (
	"archive/zip"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"math"
	"os"
	"path"
	"path/filepath"
	"strconv"
	"strings"
	"unicode/utf8"

	"ChillHub/server/internal/adminutil"

	"golang.org/x/text/encoding/charmap"
)

// maxUncompressedBytesDefault is the hard ceiling on what one archive may
// expand to on disk.
//
// The sizes in a ZIP header are whatever the archive says they are, so they can
// neither be trusted for a precheck nor relied upon during extraction: a small
// upload can declare tiny entries and then stream gigabytes (a zip bomb), and
// the extraction loop used a plain io.Copy with no bound at all. The real,
// written byte count is therefore counted and cut off here. The default is
// generous — the largest legitimate build is far below it — and
// BUILD_MAX_UNCOMPRESSED_BYTES can raise or lower it without a rebuild.
const maxUncompressedBytesDefault int64 = 128 << 30 // 128 GiB

// Reasons an archive is refused. They are sentinels because the publish paths
// hand them to the operator verbatim and the tests match on them; wrapping adds
// the offending entry or the limit.
var (
	errArchiveTooLarge = errors.New("archive expands beyond the allowed size")
	errZipSlip         = errors.New("zip entry outside target")
)

func maxUncompressedBytes() int64 {
	if v := strings.TrimSpace(os.Getenv("BUILD_MAX_UNCOMPRESSED_BYTES")); v != "" {
		if n, err := strconv.ParseInt(v, 10, 64); err == nil && n > 0 {
			return n
		}
	}
	return maxUncompressedBytesDefault
}

// extractBudget bounds the total number of bytes an extraction may write.
type extractBudget struct {
	limit     int64
	remaining int64
}

func newExtractBudget() *extractBudget {
	n := maxUncompressedBytes()
	return &extractBudget{limit: n, remaining: n}
}

// copy writes src into dst, counting the bytes actually produced and failing
// as soon as the budget is exhausted — the declared entry size is not consulted
// at all, so a lying header cannot get past it.
func (b *extractBudget) copy(dst io.Writer, src io.Reader) error {
	n, err := io.Copy(dst, io.LimitReader(src, b.remaining+1))
	b.remaining -= n
	if err != nil {
		return err
	}
	if b.remaining < 0 {
		return fmt.Errorf("%w (%d bytes)", errArchiveTooLarge, b.limit)
	}
	return nil
}

// estimateZipUncompressedSize sums UncompressedSize64 of all regular files in
// the ZIP. The result is only a precheck hint: the values come from the archive
// headers and may be wrong or hostile, which is why the sum saturates instead
// of wrapping around and why extraction enforces its own budget.
func estimateZipUncompressedSize(zipPath string) (uint64, error) {
	r, err := zip.OpenReader(zipPath)
	if err != nil {
		return 0, err
	}
	defer func() { _ = r.Close() }()
	var total uint64
	for _, f := range r.File {
		if f.FileInfo().IsDir() {
			continue
		}
		sz := f.UncompressedSize64
		if total+sz < total { // wrapped: crafted headers summing past 2^64
			return math.MaxUint64, nil
		}
		total += sz
	}
	return total, nil
}

// ===== ZIP filename decoding helpers =====
// Many ZIP creators on Windows do not set the UTF-8 flag and store filenames
// using a legacy codepage (CP866 or Windows-1251 for Russian). The stdlib
// falls back to CP437 in such cases which leads to mojibake for Cyrillic.
// We try to recover the correct filename by:
//  1. Preferring the Info-ZIP Unicode Path extra field (0x7075) if present.
//  2. If NonUTF8 is set and no Unicode Path is present, heuristically
//     re-interpret the CP437 string as bytes and decode using CP866/Win1251.
//  3. If the result looks like valid UTF-8 and improves Cyrillic ratio, use it.

// parseZipUnicodePath returns UTF-8 filename from the Info-ZIP Unicode Path extra field (0x7075) if present.
func parseZipUnicodePath(extra []byte) string {
	// Extra fields: [2 bytes header id][2 bytes data size][data] ...
	// 0x7075 ("up") layout: version(1), nameCRC32(4), utf8Name(rest)
	for i := 0; i+4 <= len(extra); {
		id := binary.LittleEndian.Uint16(extra[i:])
		sz := int(binary.LittleEndian.Uint16(extra[i+2:]))
		i += 4
		if i+sz > len(extra) {
			break
		}
		if id == 0x7075 && sz >= 5 {
			data := extra[i : i+sz]
			name := string(data[5:])
			if utf8.ValidString(name) && strings.TrimSpace(name) != "" {
				return name
			}
		}
		i += sz
	}
	return ""
}

// countCyrillicRunes returns how many of a string's runes are Cyrillic, and how
// many runes it has in total.
func countCyrillicRunes(s string) (int, int) {
	cyr, total := 0, 0
	for _, r := range s {
		total++
		if (r >= 'Ѐ' && r <= 'ӿ') || (r >= 'Ԁ' && r <= 'ԯ') {
			cyr++
		}
	}
	return cyr, total
}

// tryFixCyrillicFromCP437 attempts to re-decode a mojibake filename that was decoded as CP437
// by encoding it back to CP437 bytes and then decoding with CP866 or Windows-1251.
func tryFixCyrillicFromCP437(name string) string {
	// Encode current runes to CP437 bytes
	b, err := charmap.CodePage437.NewEncoder().Bytes([]byte(name))
	if err != nil {
		return name
	}
	// Try CP866
	s866, err866 := charmap.CodePage866.NewDecoder().String(string(b))
	// Try Windows-1251
	s1251, err1251 := charmap.Windows1251.NewDecoder().String(string(b))

	best := name
	bestScore := -1
	// baseline score
	if utf8.ValidString(name) {
		c, t := countCyrillicRunes(name)
		if t > 0 {
			bestScore = c * 2 // prefer Cyrillic heavy
		} else {
			bestScore = 0
		}
	}
	if err866 == nil && utf8.ValidString(s866) {
		c, _ := countCyrillicRunes(s866)
		score := c * 2
		if score > bestScore {
			bestScore = score
			best = s866
		}
	}
	if err1251 == nil && utf8.ValidString(s1251) {
		c, _ := countCyrillicRunes(s1251)
		score := c * 2
		if score > bestScore {
			best = s1251
		}
	}
	return best
}

// zipFileDecodedName returns the best-effort UTF-8 filename for a zip.File.
func zipFileDecodedName(f *zip.File) string {
	if n := parseZipUnicodePath(f.Extra); n != "" {
		return n
	}
	// If the UTF-8 flag is not set, the stdlib assumed CP437; try to fix Cyrillic
	if f.NonUTF8 {
		return tryFixCyrillicFromCP437(f.Name)
	}
	return f.Name
}

// zipEntryRelPath normalizes an archive entry name into a relative slash path,
// or "" when the entry should be skipped.
//
// A BACKSLASH IN AN ENTRY NAME IS TREATED AS A DIRECTORY SEPARATOR, on every
// platform. The ZIP specification (APPNOTE 4.4.17.1) allows only the forward
// slash, so "dir\file.txt" only ever comes from a tool that wrote the Windows
// separator by mistake — and the old normalization went through
// filepath.ToSlash, which resolves it on Windows and does NOTHING on Linux. The
// same archive therefore extracted into a directory tree on a developer's
// machine and into one file literally named "dir\file.txt" on the Linux
// production host, where validateManifest then rejected the whole publication
// ("backslash in path"). A build that passes review must not fail on deploy for
// that reason, so the separator is decided here rather than by the host OS.
//
// Separator, rather than a literal character, is the only reading that can
// produce an installable build: everything published here is a Windows tree, and
// Windows cannot hold a file whose name contains a backslash, so the client could
// never write such a file even if the manifest allowed it. It is also the safer
// reading — the traversal guard below then runs over the real segments, so
// "..\..\evil" is collapsed by Clean instead of surviving as an odd file name.
//
// path.Clean, not filepath.Clean, for the same reason: the result must not depend
// on which OS the server runs on.
func zipEntryRelPath(f *zip.File) string {
	name := strings.ReplaceAll(strings.TrimSpace(zipFileDecodedName(f)), "\\", "/")
	// remove any leading slashes so the entry is anchored inside the target
	rel := strings.TrimLeft(name, "/")
	// collapse any . and .. segments
	rel = path.Clean(rel)
	if rel == "." || rel == "" {
		return ""
	}
	return rel
}

// unzipTo extracts a .zip archive into target directory, preserving structure.
func unzipTo(zipPath, target string) error {
	r, err := zip.OpenReader(zipPath)
	if err != nil {
		return err
	}
	defer func() { _ = r.Close() }()
	budget := newExtractBudget()
	for _, f := range r.File {
		// normalize entry name and guard against ZipSlip
		rel := zipEntryRelPath(f)
		if rel == "" {
			continue
		}
		// ensure final destination is within target
		full := filepath.Join(target, rel)
		if !adminutil.EnsureWithin(target, full) {
			return fmt.Errorf("%w: %s", errZipSlip, rel)
		}
		// directory entry: check header info and suffix
		if f.FileInfo().IsDir() || strings.HasSuffix(rel, "/") {
			if err := makeEntryDir(full); err != nil {
				return fmt.Errorf("mkdir dir failed for entry %q -> %s: %w", rel, full, err)
			}
			continue
		}
		if err := extractZipEntry(f, rel, full, budget); err != nil {
			return err
		}
	}
	return nil
}

// makeEntryDir creates the directory an archive entry asks for, recovering from
// the two states a sloppy archive leaves behind: a missing parent, and a plain
// file sitting where the directory has to go.
func makeEntryDir(full string) error {
	if err := os.MkdirAll(full, contentDirPerm); err == nil {
		return nil
	}
	// Fallbacks: ensure parent exists, handle possible file-vs-dir collision.
	_ = os.MkdirAll(filepath.Dir(full), contentDirPerm)
	err := os.MkdirAll(full, contentDirPerm)
	if err == nil {
		return nil
	}
	// if a file exists at 'full', remove it and retry
	if st, e := os.Stat(full); e == nil && !st.IsDir() {
		_ = os.Remove(full)
		if err3 := os.MkdirAll(full, contentDirPerm); err3 == nil {
			return nil
		}
	}
	return err
}

// makeEntryParent creates the directory that will hold an extracted file,
// removing a plain file that occupies its place.
func makeEntryParent(full string) error {
	parent := filepath.Dir(full)
	err := os.MkdirAll(parent, contentDirPerm)
	if err == nil {
		return nil
	}
	// handle possible file-vs-dir collision on parent
	if st, e := os.Stat(parent); e == nil && !st.IsDir() {
		_ = os.Remove(parent)
		if err2 := os.MkdirAll(parent, contentDirPerm); err2 == nil {
			return nil
		}
	}
	return fmt.Errorf("mkdir parent failed for %s: %w", parent, err)
}

// extractZipEntry writes one regular file entry to disk under the budget.
func extractZipEntry(f *zip.File, rel, full string, budget *extractBudget) error {
	if err := makeEntryParent(full); err != nil {
		return fmt.Errorf("entry %q: %w", rel, err)
	}
	rc, err := f.Open()
	if err != nil {
		return fmt.Errorf("open zip entry %q: %w", rel, err)
	}
	defer func() { _ = rc.Close() }()
	out, err := os.OpenFile(full, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, contentFilePerm)
	if err != nil {
		return fmt.Errorf("create file failed for entry %q -> %s: %w", rel, full, err)
	}
	if err := budget.copy(out, rc); err != nil {
		_ = out.Close()
		return fmt.Errorf("write file failed for entry %q -> %s: %w", rel, full, err)
	}
	// A Close that fails on a file we just wrote is a lost write, not noise: on
	// a full or failing volume this is where the error surfaces.
	if err := out.Close(); err != nil {
		return fmt.Errorf("write file failed for entry %q -> %s: %w", rel, full, err)
	}
	return nil
}

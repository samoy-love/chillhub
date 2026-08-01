package builds

import (
	"archive/zip"
	"encoding/binary"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"unicode/utf8"

	"ChillHub/server/internal/adminutil"

	"golang.org/x/text/encoding/charmap"
)

// estimateZipUncompressedSize sums UncompressedSize64 of all regular files in the ZIP.
func estimateZipUncompressedSize(zipPath string) (uint64, error) {
	r, err := zip.OpenReader(zipPath)
	if err != nil {
		return 0, err
	}
	defer r.Close()
	var total uint64
	for _, f := range r.File {
		if f.FileInfo().IsDir() {
			continue
		}
		total += f.UncompressedSize64
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

func countCyrillicRunes(s string) (cyr, total int) {
	for _, r := range s {
		total++
		if (r >= 'Ѐ' && r <= 'ӿ') || (r >= 'Ԁ' && r <= 'ԯ') {
			cyr++
		}
	}
	return
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
func zipEntryRelPath(f *zip.File) string {
	name := zipFileDecodedName(f)
	// remove any drive letters or leading slashes/backslashes
	rel := filepath.ToSlash(strings.TrimLeft(strings.TrimSpace(name), "/\\"))
	// collapse any .. segments
	rel = filepath.ToSlash(filepath.Clean(rel))
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
	defer r.Close()
	for _, f := range r.File {
		// normalize entry name and guard against ZipSlip
		rel := zipEntryRelPath(f)
		if rel == "" {
			continue
		}
		// ensure final destination is within target
		full := filepath.Join(target, rel)
		if !adminutil.EnsureWithin(target, full) {
			return fmt.Errorf("zip entry outside target: %s", rel)
		}
		// directory entry: check header info and suffix
		if f.FileInfo().IsDir() || strings.HasSuffix(rel, "/") {
			if err := os.MkdirAll(full, 0o755); err != nil {
				// Fallbacks: ensure parent exists, handle possible file-vs-dir collision
				_ = os.MkdirAll(filepath.Dir(full), 0o755)
				if err2 := os.MkdirAll(full, 0o755); err2 != nil {
					// if a file exists at 'full', remove it and retry
					if st, e := os.Stat(full); e == nil && !st.IsDir() {
						_ = os.Remove(full)
						if err3 := os.MkdirAll(full, 0o755); err3 == nil {
							continue
						}
					}
					return fmt.Errorf("mkdir dir failed for entry %q -> %s: %w", rel, full, err2)
				}
			}
			continue
		}
		// ensure directory exists
		if err := os.MkdirAll(filepath.Dir(full), 0o755); err != nil {
			// handle possible file-vs-dir collision on parent
			parent := filepath.Dir(full)
			if st, e := os.Stat(parent); e == nil && !st.IsDir() {
				_ = os.Remove(parent)
				if err2 := os.MkdirAll(parent, 0o755); err2 != nil {
					return fmt.Errorf("mkdir parent failed for entry %q -> %s: %w", rel, parent, err)
				}
			} else {
				return fmt.Errorf("mkdir parent failed for entry %q -> %s: %w", rel, parent, err)
			}
		}
		rc, err := f.Open()
		if err != nil {
			return fmt.Errorf("open zip entry %q: %w", rel, err)
		}
		out, err := os.OpenFile(full, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o644)
		if err != nil {
			rc.Close()
			return fmt.Errorf("create file failed for entry %q -> %s: %w", rel, full, err)
		}
		if _, err := io.Copy(out, rc); err != nil {
			out.Close()
			rc.Close()
			return fmt.Errorf("write file failed for entry %q -> %s: %w", rel, full, err)
		}
		out.Close()
		rc.Close()
	}
	return nil
}

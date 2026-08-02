package builds

import (
	"archive/zip"
	"bytes"
	"encoding/binary"
	"hash/crc32"
	"io"
	"os"
	"path/filepath"
	"testing"

	"golang.org/x/text/encoding/charmap"
)

// unicodePathExtra builds an Info-ZIP Unicode Path (0x7075) extra field.
func unicodePathExtra(legacyName, utf8Name string) []byte {
	data := make([]byte, 0, 5+len(utf8Name))
	data = append(data, 1) // version
	var crc [4]byte
	binary.LittleEndian.PutUint32(crc[:], crc32.ChecksumIEEE([]byte(legacyName)))
	data = append(data, crc[:]...)
	data = append(data, utf8Name...)

	out := make([]byte, 4, 4+len(data))
	binary.LittleEndian.PutUint16(out[0:], 0x7075)
	binary.LittleEndian.PutUint16(out[2:], uint16(len(data)))
	return append(out, data...)
}

// A ZIP written by a Russian-locale Windows tool stores the file name in a
// legacy codepage and puts the real UTF-8 name in an extra field. The stdlib
// hands back the CP437 mojibake. Preferring the extra field is what keeps the
// name in the manifest equal to the name on disk — and the manifest path is the
// download URL, so a mismatch is a 404 on install for every user.
func TestZipNameTakesTheInfoZipUnicodePath(t *testing.T) {
	const legacy = "\x8f\xa0\xaa\xa5\xe2.dat" // "Пакет.dat" in CP866
	const real = "Пакет.dat"
	f := &zip.File{FileHeader: zip.FileHeader{
		Name:    legacy,
		NonUTF8: true,
		Extra:   unicodePathExtra(legacy, real),
	}}
	if got := zipFileDecodedName(f); got != real {
		t.Fatalf("decoded %q, want %q", got, real)
	}
}

// Extra fields must not be trusted blindly: a malformed 0x7075 field (declared
// longer than the buffer, or holding invalid UTF-8) has to be ignored so the
// decoder falls back instead of producing a name that cannot be written to disk.
func TestZipNameIgnoresBrokenUnicodePathField(t *testing.T) {
	extra := unicodePathExtra("x.txt", "x.txt")
	binary.LittleEndian.PutUint16(extra[2:], uint16(len(extra))) // size past the end
	if got := parseZipUnicodePath(extra); got != "" {
		t.Errorf("a truncated extra field yielded %q", got)
	}
	if got := parseZipUnicodePath(unicodePathExtra("x", "\xff\xfe not utf8")); got != "" {
		t.Errorf("invalid UTF-8 accepted from the extra field: %q", got)
	}
	if got := parseZipUnicodePath(nil); got != "" {
		t.Errorf("empty extra yielded %q", got)
	}
}

// Without a Unicode Path field the stdlib has already decoded a CP866 name as
// CP437, producing mojibake. Recovering it matters because the alternative is a
// build published under an unreadable name that no client can request; Cyrillic
// asset names are routine in this project.
func TestCyrillicNameIsRecoveredFromCP437Mojibake(t *testing.T) {
	const real = "Данные/уровень.dat"
	cp866, err := charmap.CodePage866.NewEncoder().String(real)
	if err != nil {
		t.Fatal(err)
	}
	mojibake, err := charmap.CodePage437.NewDecoder().String(cp866)
	if err != nil {
		t.Fatal(err)
	}
	if mojibake == real {
		t.Fatal("the fixture is not mojibake; the test would prove nothing")
	}
	f := &zip.File{FileHeader: zip.FileHeader{Name: mojibake, NonUTF8: true}}
	if got := zipFileDecodedName(f); got != real {
		t.Fatalf("recovered %q, want %q", got, real)
	}
}

// The recovery heuristic scores candidates by how much Cyrillic they contain, so
// it must leave a plain ASCII name completely alone. Rewriting "ChillHub.exe"
// into something else would break the one file the launcher needs most.
func TestAsciiNamesSurviveTheCyrillicHeuristic(t *testing.T) {
	for _, name := range []string{"ChillHub.exe", "runtimes/win-x64/native/blake3_dotnet.dll", "data/level-1.dat"} {
		f := &zip.File{FileHeader: zip.FileHeader{Name: name, NonUTF8: true}}
		if got := zipFileDecodedName(f); got != name {
			t.Errorf("ASCII name %q was rewritten to %q", name, got)
		}
	}
}

// A name that is already correct UTF-8 (the UTF-8 flag is set) must be used
// verbatim — running the CP437 heuristic over it would corrupt it.
func TestUtf8FlaggedNamesAreUsedVerbatim(t *testing.T) {
	const name = "данные/файл.dat"
	f := &zip.File{FileHeader: zip.FileHeader{Name: name, NonUTF8: false}}
	if got := zipFileDecodedName(f); got != name {
		t.Fatalf("decoded %q, want %q", got, name)
	}
}

// Entry names are turned into filesystem paths, so the normalisation has to
// collapse every way an archive can express "not a relative path inside the
// target". Each of these once produced either a write outside the tree or a
// manifest path that no longer matched the file on disk.
func TestZipEntryRelPathNormalisation(t *testing.T) {
	cases := map[string]string{
		"  spaced/app.dll  ": "spaced/app.dll",
		"/leading/slash.txt": "leading/slash.txt",
		"./dot/app.dll":      "dot/app.dll",
		"a/./b/c.txt":        "a/b/c.txt",
		".":                  "",
		"":                   "",
		"/":                  "",
	}
	for in, want := range cases {
		f := &zip.File{FileHeader: zip.FileHeader{Name: in}}
		if got := zipEntryRelPath(f); got != want {
			t.Errorf("%q -> %q, want %q", in, got, want)
		}
	}
}

// A directory entry carries no bytes but must still create the directory: game
// builds ship empty mod/save folders, and the manifest can only describe them
// once they exist in the extracted tree.
func TestExtractionCreatesExplicitDirectoryEntries(t *testing.T) {
	root := t.TempDir()
	target := filepath.Join(root, "files")
	mustMkdirAll(t, target)

	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	if _, err := zw.CreateHeader(&zip.FileHeader{Name: "mods/"}); err != nil {
		t.Fatal(err)
	}
	w, err := zw.Create("app.txt")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := io.WriteString(w, "x"); err != nil {
		t.Fatal(err)
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	zipPath := filepath.Join(root, "b.zip")
	if err := os.WriteFile(zipPath, buf.Bytes(), 0o644); err != nil {
		t.Fatal(err)
	}

	if err := unzipTo(zipPath, target); err != nil {
		t.Fatalf("unzip: %v", err)
	}
	st, err := os.Stat(filepath.Join(target, "mods"))
	if err != nil || !st.IsDir() {
		t.Fatalf("the empty directory entry was dropped: %v", err)
	}
	_, emptyDirs, err := scanManifest(target)
	if err != nil {
		t.Fatal(err)
	}
	if len(emptyDirs) != 1 || emptyDirs[0] != "mods/" {
		t.Fatalf("emptyDirs = %v, want [mods/]", emptyDirs)
	}
}

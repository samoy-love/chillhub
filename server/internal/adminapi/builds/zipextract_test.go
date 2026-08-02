package builds

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

// zipWithNames builds an archive whose entry names are written verbatim, so a
// test can plant names a normal zip writer would refuse or rewrite.
func zipWithNames(t *testing.T, entries [][2]string) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	for _, e := range entries {
		w, err := zw.CreateHeader(&zip.FileHeader{Name: e[0], Method: zip.Deflate})
		if err != nil {
			t.Fatal(err)
		}
		if _, err := io.WriteString(w, e[1]); err != nil {
			t.Fatal(err)
		}
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

// A ZIP entry that walks out of the extraction root must abort the publication.
// The extraction root sits inside the content tree, next to every other game's
// published files, so one crafted archive would otherwise overwrite another
// game's build — or anything else the service can write.
func TestExtractionRefusesEntriesOutsideTheTarget(t *testing.T) {
	root := t.TempDir()
	target := filepath.Join(root, "content", "game", "1.0.0", "files")
	mustMkdirAll(t, target)
	canary := filepath.Join(root, "content", "victim.txt")
	mustWriteFile(t, canary, "untouched")

	zipPath := filepath.Join(root, "evil.zip")
	mustWriteFile(t, zipPath, "")
	if err := os.WriteFile(zipPath, zipWithNames(t, [][2]string{
		{"../../victim.txt", "pwned"},
		{"ok.txt", "fine"},
	}), 0o644); err != nil {
		t.Fatal(err)
	}

	if err := unzipTo(zipPath, target); err == nil {
		t.Fatal("an archive escaping the extraction root was accepted")
	}
	b, err := os.ReadFile(canary)
	if err != nil || string(b) != "untouched" {
		t.Fatalf("a file outside the extraction root was overwritten: %v %q", err, string(b))
	}
}

// The same guard has to hold on the streaming path, which extracts through a
// separate function. A traversal entry there must produce an error event rather
// than a silently truncated stream the admin UI reads as success.
func TestStreamUnzipRefusesEntriesOutsideTheTarget(t *testing.T) {
	root := t.TempDir()
	target := filepath.Join(root, "files")
	mustMkdirAll(t, target)
	zipPath := filepath.Join(root, "evil.zip")
	if err := os.WriteFile(zipPath, zipWithNames(t, [][2]string{{"../escape.txt", "pwned"}}), 0o644); err != nil {
		t.Fatal(err)
	}

	var sink bytes.Buffer
	if streamUnzip(&sink, adminutilNopFlusher{}, zipPath, target) {
		t.Fatal("streamUnzip reported success for a traversal entry")
	}
	if !strings.Contains(sink.String(), `"type":"error"`) {
		t.Fatalf("no error event emitted: %q", sink.String())
	}
	if _, err := os.Stat(filepath.Join(root, "escape.txt")); err == nil {
		t.Fatal("the traversal entry was written outside the target")
	}
}

// Absolute paths and drive-relative leading separators are common in archives
// produced by careless tooling. They must be reinterpreted as relative to the
// extraction root, not rejected and not followed: an archive built with "zip -y
// /opt/build/..." is a legitimate build, and refusing it outright would be a
// publication outage.
func TestExtractionAnchorsAbsoluteEntriesInsideTheTarget(t *testing.T) {
	root := t.TempDir()
	target := filepath.Join(root, "files")
	mustMkdirAll(t, target)
	zipPath := filepath.Join(root, "abs.zip")
	if err := os.WriteFile(zipPath, zipWithNames(t, [][2]string{
		{"/opt/build/app.dll", "payload"},
	}), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := unzipTo(zipPath, target); err != nil {
		t.Fatalf("unzip: %v", err)
	}
	if _, err := os.Stat(filepath.Join(target, "opt", "build", "app.dll")); err != nil {
		t.Fatalf("absolute entry did not land under the target: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "opt")); err == nil {
		t.Fatal("the entry was written relative to the filesystem root")
	}
}

// A truncated upload — the browser tab closed halfway, the connection dropped —
// arrives as a file that is not a valid ZIP. What matters is not the status code
// but that the volume is left exactly as it was: no staging tree, no temp
// archive, no published version. These leak per attempt, and on a content volume
// holding 30 GB uploads they add up fast.
func TestTruncatedArchiveLeavesNothingBehind(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	good := zipBytes(t, map[string]string{"a.txt": "hello", "b/c.txt": "world"})

	w := httptest.NewRecorder()
	h.Upload(w, uploadRequest(t, "game", "1.0.0", good[:len(good)/2]))
	if w.Code == http.StatusOK {
		t.Fatalf("a truncated archive was published: %s", w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); err == nil {
		t.Error("a version directory was published from a truncated archive")
	}
	if _, err := os.Stat(filepath.Join(root, "manifests", "game", "1.0.0.json")); err == nil {
		t.Error("a manifest was written for a truncated archive")
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
	assertTmpDirEmpty(t, root)
}

// Extraction that dies in the middle — here forced by the uncompressed-size
// ceiling after some entries are already on disk — must not leave the partially
// written tree anywhere a client can reach. The staging directory exists for
// exactly this, and the failure path has to actually remove it.
func TestExtractionAbortedMidwayLeavesNoPartialTree(t *testing.T) {
	t.Setenv("BUILD_MAX_UNCOMPRESSED_BYTES", "64")
	root := t.TempDir()
	h := New(root)

	w := httptest.NewRecorder()
	h.Upload(w, uploadRequest(t, "game", "1.0.0", zipBytes(t, map[string]string{
		"aaa-small.txt": "tiny",
		"zzz-big.bin":   strings.Repeat("A", 4096),
	})))
	if w.Code == http.StatusOK {
		t.Fatalf("the oversized archive was published: %s", w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); err == nil {
		t.Error("a half-extracted build is live")
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
	assertTmpDirEmpty(t, root)
}

// estimateZipUncompressedSize decides whether the volume can hold the build
// before a single byte is extracted. Under-reporting means the precheck waves an
// archive through and the volume fills up halfway — which on this host takes the
// public API and the neighbouring sites down with it. Directory entries carry no
// payload and must not be counted; a size that is short by the file count is
// still short.
func TestUncompressedSizeEstimateSumsPayloadOnly(t *testing.T) {
	root := t.TempDir()
	zipPath := filepath.Join(root, "b.zip")

	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	if _, err := zw.CreateHeader(&zip.FileHeader{Name: "mods/"}); err != nil {
		t.Fatal(err)
	}
	var want uint64
	for _, body := range []string{strings.Repeat("a", 5000), "", strings.Repeat("b", 17)} {
		w, err := zw.Create("f" + strconv.Itoa(int(want)) + ".bin")
		if err != nil {
			t.Fatal(err)
		}
		if _, err := io.WriteString(w, body); err != nil {
			t.Fatal(err)
		}
		want += uint64(len(body))
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(zipPath, buf.Bytes(), 0o644); err != nil {
		t.Fatal(err)
	}

	got, err := estimateZipUncompressedSize(zipPath)
	if err != nil {
		t.Fatalf("estimate: %v", err)
	}
	if got != want {
		t.Fatalf("estimate = %d, want %d", got, want)
	}
}

// The panel shows these numbers to decide whether a build can be uploaded at
// all, so they have to be real. The response must also stay free of the content
// root: it is an absolute server path, the panel never displays it, and leaking
// filesystem layout through an admin endpoint is how a foothold becomes useful.
func TestFreeSpaceReportsNumbersWithoutLeakingThePath(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	w := httptest.NewRecorder()
	h.FreeSpace(w, httptest.NewRequest(http.MethodGet, "http://example.com/admin/api/freespace", nil))
	if w.Code != http.StatusOK {
		t.Fatalf("freespace: %d %s", w.Code, w.Body.String())
	}
	var out map[string]any
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatalf("not JSON: %v (%s)", err, w.Body.String())
	}
	bytesFree, ok := out["bytes"].(float64)
	if !ok || bytesFree <= 0 {
		t.Fatalf("bytes = %v; the panel cannot size an upload against that", out["bytes"])
	}
	if strings.Contains(w.Body.String(), filepath.ToSlash(root)) || strings.Contains(w.Body.String(), root) {
		t.Fatalf("the content root leaked into the response: %s", w.Body.String())
	}
}

// A body that is not an archive at all must be reported as an error, not as
// "needs zero bytes". The publish handlers skip the space precheck when the
// estimate fails — silently returning 0 would instead assert that a corrupt
// upload is guaranteed to fit.
func TestUncompressedSizeEstimateFailsOnANonArchive(t *testing.T) {
	p := filepath.Join(t.TempDir(), "not.zip")
	mustWriteFile(t, p, "definitely not a zip")
	if _, err := estimateZipUncompressedSize(p); err == nil {
		t.Fatal("a non-archive was estimated without an error")
	}
}

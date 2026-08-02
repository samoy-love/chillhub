package builds

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"sync"
	"testing"
)

// zipBytes builds an in-memory ZIP with the given path -> content entries.
func zipBytes(t *testing.T, entries map[string]string) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	for name, body := range entries {
		w, err := zw.Create(name)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := io.WriteString(w, body); err != nil {
			t.Fatal(err)
		}
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

// uploadRequest builds a multipart POST for the plain /admin/api/upload endpoint.
func uploadRequest(t *testing.T, gid, ver string, zipData []byte) *http.Request {
	t.Helper()
	var body bytes.Buffer
	mw := multipart.NewWriter(&body)
	_ = mw.WriteField("kind", "game")
	_ = mw.WriteField("gameId", gid)
	_ = mw.WriteField("version", ver)
	fw, err := mw.CreateFormFile("zip", "build.zip")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := fw.Write(zipData); err != nil {
		t.Fatal(err)
	}
	if err := mw.Close(); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/upload", &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// A successful plain upload must publish the complete tree, and must not leave
// staging directories behind.
func TestUploadPublishesViaStaging(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	w := httptest.NewRecorder()
	h.Upload(w, uploadRequest(t, "game", "1.0.0", zipBytes(t, map[string]string{
		"a.txt":     "hello",
		"sub/b.txt": "world",
	})))
	if w.Code != http.StatusOK {
		t.Fatalf("upload failed: %d %s", w.Code, w.Body.String())
	}
	var m manifest
	if err := json.Unmarshal(w.Body.Bytes(), &m); err != nil {
		t.Fatalf("manifest json: %v", err)
	}
	if len(m.Files) != 2 {
		t.Fatalf("expected 2 files in manifest, got %d", len(m.Files))
	}
	final := filepath.Join(root, "content", "game", "1.0.0", "files")
	for _, rel := range []string{"a.txt", "sub/b.txt"} {
		if _, err := os.Stat(filepath.Join(final, filepath.FromSlash(rel))); err != nil {
			t.Fatalf("%s not published: %v", rel, err)
		}
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
}

// A broken archive must leave NOTHING published: before staging was introduced
// here, Upload created content/<gid>/<ver>/files up front and the failed
// extraction left a half-built version live.
func TestUploadFailureLeavesNoPublishedVersion(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	w := httptest.NewRecorder()
	h.Upload(w, uploadRequest(t, "game", "1.0.0", []byte("this is not a zip file at all")))
	if w.Code == http.StatusOK {
		t.Fatal("expected the broken archive to be rejected")
	}
	verDir := filepath.Join(root, "content", "game", "1.0.0")
	if _, err := os.Stat(verDir); err == nil {
		t.Fatalf("failed upload published %s", verDir)
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
}

// A failed upload must not destroy the version that is already live.
func TestUploadFailureKeepsPreviousVersion(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	w := httptest.NewRecorder()
	h.Upload(w, uploadRequest(t, "game", "1.0.0", zipBytes(t, map[string]string{"good.txt": "v1"})))
	if w.Code != http.StatusOK {
		t.Fatalf("first upload failed: %d %s", w.Code, w.Body.String())
	}
	w = httptest.NewRecorder()
	h.Upload(w, uploadRequest(t, "game", "1.0.0", []byte("not a zip")))
	if w.Code == http.StatusOK {
		t.Fatal("expected the broken archive to be rejected")
	}
	b, err := os.ReadFile(filepath.Join(root, "content", "game", "1.0.0", "files", "good.txt"))
	if err != nil || string(b) != "v1" {
		t.Fatalf("previously published build was damaged: %v %q", err, string(b))
	}
}

// The plain upload must spool the archive inside the content root, not into
// the system temp directory: a 30 GB body would otherwise fill the root
// partition while the free-space precheck measures the content volume.
func TestUploadSpoolsIntoContentRootAndCleansUp(t *testing.T) {
	root := t.TempDir()
	sysTmp := t.TempDir()
	// os.CreateTemp("") would land here; nothing must.
	t.Setenv("TMPDIR", sysTmp)
	t.Setenv("TMP", sysTmp)
	t.Setenv("TEMP", sysTmp)
	h := New(root)

	w := httptest.NewRecorder()
	h.Upload(w, uploadRequest(t, "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "hello"})))
	if w.Code != http.StatusOK {
		t.Fatalf("upload failed: %d %s", w.Code, w.Body.String())
	}
	if entries, err := os.ReadDir(sysTmp); err == nil && len(entries) != 0 {
		t.Fatalf("upload wrote to the system temp dir: %v", entries)
	}
	// The scratch copy must not survive the request either.
	if entries, err := os.ReadDir(filepath.Join(root, "tmp")); err == nil {
		for _, e := range entries {
			t.Fatalf("temp zip left behind: %s", e.Name())
		}
	}
}

// Two publications of the same gameId+version must not interleave: promote
// deletes every *.old-* backup before creating its own, so unsynchronised runs
// destroyed each other's backup and could pair one build's content with the
// other build's manifest.
func TestConcurrentUploadsOfSameVersionAreSerialised(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	const n = 4
	var wg sync.WaitGroup
	codes := make([]int, n)
	for i := range n {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			w := httptest.NewRecorder()
			h.Upload(w, uploadRequest(t, "game", "1.0.0", zipBytes(t, map[string]string{
				"a.txt":                       "payload",
				fmt.Sprintf("only-%d.txt", i): "x",
			})))
			codes[i] = w.Code
		}(i)
	}
	wg.Wait()
	for i, c := range codes {
		if c != http.StatusOK {
			t.Fatalf("upload %d failed: %d", i, c)
		}
	}

	// The manifest that ended up published must describe the tree that ended up
	// published: exactly one only-N.txt, and it must exist on disk.
	b, err := os.ReadFile(filepath.Join(root, "manifests", "game", "1.0.0.json"))
	if err != nil {
		t.Fatal(err)
	}
	var m manifest
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatal(err)
	}
	files := filepath.Join(root, "content", "game", "1.0.0", "files")
	for _, f := range m.Files {
		if _, err := os.Stat(filepath.Join(files, filepath.FromSlash(f.Path))); err != nil {
			t.Fatalf("manifest lists %s but the published tree does not have it: %v", f.Path, err)
		}
	}
	entries, err := os.ReadDir(files)
	if err != nil {
		t.Fatal(err)
	}
	if len(entries) != len(m.Files) {
		t.Fatalf("published tree has %d files, manifest lists %d", len(entries), len(m.Files))
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
}

// assertNoStagingLeftovers fails if any *.tmp-* staging directory survived.
func assertNoStagingLeftovers(t *testing.T, parent string) {
	t.Helper()
	entries, err := os.ReadDir(parent)
	if err != nil {
		return
	}
	for _, e := range entries {
		if matched, _ := filepath.Match("*.tmp-*", e.Name()); matched {
			t.Fatalf("staging directory left behind: %s", e.Name())
		}
	}
}

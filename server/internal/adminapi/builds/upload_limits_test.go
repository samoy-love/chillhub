package builds

import (
	"bytes"
	"errors"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// uploadRequestParts builds a multipart POST whose "zip" part is repeated once
// per element of zips. The plain uploadRequest helper cannot express that, and
// a duplicated part is exactly what the R6 regression is about.
func uploadRequestParts(t *testing.T, gid, ver string, zips ...[]byte) *http.Request {
	t.Helper()
	var body bytes.Buffer
	mw := multipart.NewWriter(&body)
	_ = mw.WriteField("kind", "game")
	_ = mw.WriteField("gameId", gid)
	_ = mw.WriteField("version", ver)
	for i, z := range zips {
		fw, err := mw.CreateFormFile("zip", "build.zip")
		if err != nil {
			t.Fatal(err)
		}
		if _, err := fw.Write(z); err != nil {
			t.Fatalf("zip part %d: %v", i, err)
		}
	}
	if err := mw.Close(); err != nil {
		t.Fatal(err)
	}
	req := httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/upload", &body)
	req.Header.Set("Content-Type", mw.FormDataContentType())
	return req
}

// stubFreeSpace replaces the free-space probe for the duration of one test.
// A full volume cannot be arranged on the machine running the suite, so the
// only way to exercise the 507 paths at all is to lie about the disk.
func stubFreeSpace(t *testing.T, fn func(string) (uint64, error)) {
	t.Helper()
	prev := freeSpaceFn
	freeSpaceFn = fn
	t.Cleanup(func() { freeSpaceFn = prev })
}

// An archive past the ceiling must be refused with 413 and must not leave the
// bytes it did manage to write on disk. Before the limit existed the copy had
// no upper bound at all: one client could write until /srv was full and take
// down every service on the host with it.
func TestUploadOversizedArchiveIsRejectedAndCleanedUp(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }

	prev := uploadZipLimit
	uploadZipLimit = 512
	t.Cleanup(func() { uploadZipLimit = prev })

	payload := bytes.Repeat([]byte("A"), 64<<10)
	w := httptest.NewRecorder()
	h.UploadStream(w, uploadRequestParts(t, "game", "1.0.0", payload))

	if w.Code != http.StatusRequestEntityTooLarge {
		t.Fatalf("expected 413 for an oversized archive, got %d: %s", w.Code, w.Body.String())
	}
	assertNoPublishScratch(t, root)
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); err == nil {
		t.Fatal("a rejected upload published a version")
	}
}

// THE R6 regression: a body with two "zip" parts used to overwrite tmpName with
// the second temp file's path, so the caller's cleanup defer removed only the
// second one and the first stayed in tmp forever. The request must be refused
// and tmp must be empty afterwards — both files, not just the last.
func TestUploadDuplicateZipPartLeavesNoScratchFiles(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }

	first := zipBytes(t, map[string]string{"a.txt": "first"})
	second := zipBytes(t, map[string]string{"b.txt": "second"})

	w := httptest.NewRecorder()
	h.UploadStream(w, uploadRequestParts(t, "game", "1.0.0", first, second))

	// Поток уже начался, и отказ едет СОБЫТИЕМ: заголовки ушли, кода ответа
	// больше нет. Панель читает поток без события "error" как успешную
	// публикацию — значит, молчание здесь хуже любого кода.
	msg, refused := streamFailure(w.Body.String())
	if !refused {
		t.Fatalf("дубль zip-части принят: %s", w.Body.String())
	}
	if !strings.Contains(msg, "duplicate zip part") {
		t.Fatalf("the client cannot tell what went wrong: %q", msg)
	}
	assertNoPublishScratch(t, root)
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); err == nil {
		t.Fatal("an ambiguous request published a version")
	}
}

// The two guards must not break the ordinary case: a normal archive on a
// healthy volume still publishes, and still leaves no scratch behind.
func TestUploadUnderLimitStillPublishes(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }
	stubFreeSpace(t, func(string) (uint64, error) { return 500 << 30, nil })

	w := httptest.NewRecorder()
	h.UploadStream(w, uploadRequestParts(t, "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "hello"})))

	if w.Code != http.StatusOK {
		t.Fatalf("normal upload broke: %d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0", "files", "a.txt")); err != nil {
		t.Fatalf("build not published: %v", err)
	}
	assertNoPublishScratch(t, root)
}

// A volume with nothing but the reserve left must be refused before a single
// byte is written. Filling the partition and only then failing is what takes
// the neighbouring services down.
func TestUploadRefusesWhenVolumeIsAlreadyFull(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }
	stubFreeSpace(t, func(string) (uint64, error) { return uploadFreeSpaceReserveBytes, nil })

	w := httptest.NewRecorder()
	h.UploadStream(w, uploadRequestParts(t, "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "hello"})))

	if w.Code != http.StatusInsufficientStorage {
		t.Fatalf("expected 507 on a full volume, got %d: %s", w.Code, w.Body.String())
	}
	assertNoPublishScratch(t, root)
}

// Space that runs out DURING the copy must stop it too. A ceiling derived from
// Content-Length cannot catch this: the disk is shared, so another writer can
// consume the room after the request was admitted — and a chunked body has no
// Content-Length to check in the first place.
func TestUploadStopsWhenSpaceRunsOutMidCopy(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }
	// Just enough budget (reserve + 16 bytes) that the precheck passes and the
	// very first buffer of the archive exceeds it.
	stubFreeSpace(t, func(string) (uint64, error) { return uploadFreeSpaceReserveBytes + 16, nil })

	w := httptest.NewRecorder()
	h.UploadStream(w, uploadRequestParts(t, "game", "1.0.0", bytes.Repeat([]byte("A"), 8<<10)))

	if w.Code != http.StatusInsufficientStorage {
		t.Fatalf("expected 507 when the volume fills up mid-copy, got %d: %s", w.Code, w.Body.String())
	}
	assertNoPublishScratch(t, root)
}

// A disk we cannot measure must not block publishing: freeSpaceBytes fails on
// exotic filesystems and in some containers, and refusing every upload there
// would be a worse outage than the one the guard prevents.
func TestUploadProceedsWhenFreeSpaceIsUnknown(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }
	stubFreeSpace(t, func(string) (uint64, error) { return 0, errors.New("statfs not supported") })

	w := httptest.NewRecorder()
	h.UploadStream(w, uploadRequestParts(t, "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "hello"})))

	if w.Code != http.StatusOK {
		t.Fatalf("an unmeasurable volume blocked a valid upload: %d %s", w.Code, w.Body.String())
	}
	assertNoPublishScratch(t, root)
}
// stubTightContentVolume makes the volume that will hold the EXTRACTED tree look
// almost full while the spool volume stays roomy. Both are measured through the
// same probe, so they are told apart by their path: the extraction root always
// lives under <root>/content, the spool file under <root>/tmp.
func stubTightContentVolume(t *testing.T, contentFree uint64) {
	t.Helper()
	stubFreeSpace(t, func(p string) (uint64, error) {
		if strings.Contains(filepath.ToSlash(p), "/content/") {
			return contentFree, nil
		}
		return 500 << 30, nil
	})
}

// The same guard on the streaming path, where the refusal has to travel as an
// NDJSON error event rather than a status line: the admin UI reads a stream with
// no error event as a successful publication.
func TestUploadStreamRefusesAnArchiveTooBigForTheContentVolume(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }
	h.CurrentUser = func(*http.Request) string { return "admin" }
	stubTightContentVolume(t, 4096)

	w := httptest.NewRecorder()
	h.UploadStream(w, streamUploadRequest(t,
		map[string]string{"kind": "game", "gameId": "game", "version": "1.0.0"},
		zipBytes(t, map[string]string{"big.bin": strings.Repeat("A", 64<<10)})))

	events, garbage := ndjsonEvents(t, w.Body.String())
	if len(garbage) > 0 {
		t.Errorf("plain text injected into the NDJSON stream: %q", garbage)
	}
	if !hasErrorEvent(events) {
		t.Fatalf("no error event; the UI would report this build as published: %s", w.Body.String())
	}
	if !strings.Contains(w.Body.String(), "insufficient disk space") {
		t.Errorf("the operator cannot tell the volume was the problem: %q", w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); err == nil {
		t.Error("a build that does not fit was published anyway")
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
	assertNoTempZip(t, root)
}

// And on the chunked path, which is the one real multi-gigabyte releases use —
// i.e. the only path where the volume genuinely runs out.
func TestUploadProcessStreamRefusesAnArchiveTooBigForTheContentVolume(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }
	h.CurrentUser = func(*http.Request) string { return "admin" }
	stubTightContentVolume(t, 4096)

	id := "0123456789abcdef0123456789abcdef"
	mustMkdirAll(t, h.uploadDir(id))
	if err := os.WriteFile(h.uploadZipPath(id),
		zipBytes(t, map[string]string{"big.bin": strings.Repeat("A", 64<<10)}), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := h.writeUploadMeta(&uploadMeta{
		UploadID: id, Kind: "game", GameID: "game", Version: "1.0.0", Status: "ready",
	}); err != nil {
		t.Fatal(err)
	}

	w := httptest.NewRecorder()
	h.UploadProcessStream(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/upload/process?uploadId="+id, nil))

	events, _ := ndjsonEvents(t, w.Body.String())
	if !hasErrorEvent(events) {
		t.Fatalf("no error event: %s", w.Body.String())
	}
	if !strings.Contains(w.Body.String(), "insufficient disk space") {
		t.Errorf("the operator cannot tell the volume was the problem: %q", w.Body.String())
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0")); err == nil {
		t.Error("a build that does not fit was published anyway")
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
}

// spaceBudget decides between "refuse" and "do not enforce", and the two are
// both encoded in a (uint64, bool) pair — an easy place to invert a condition
// and either block every upload or stop guarding at all.
func TestSpaceBudget(t *testing.T) {
	cases := []struct {
		name       string
		free       uint64
		err        error
		wantBudget uint64
		wantKnown  bool
	}{
		{"probe failed: guard disabled", 0, errors.New("nope"), 0, false},
		{"filesystem reports zero: guard disabled", 0, nil, 0, false},
		{"below the reserve: nothing may be written", uploadFreeSpaceReserveBytes - 1, nil, 0, true},
		{"exactly the reserve: nothing may be written", uploadFreeSpaceReserveBytes, nil, 0, true},
		{"above the reserve: the surplus may be written", uploadFreeSpaceReserveBytes + 4096, nil, 4096, true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			stubFreeSpace(t, func(string) (uint64, error) { return tc.free, tc.err })
			budget, known := spaceBudget(t.TempDir())
			if budget != tc.wantBudget || known != tc.wantKnown {
				t.Fatalf("got (%d, %v), want (%d, %v)", budget, known, tc.wantBudget, tc.wantKnown)
			}
		})
	}
}

// The real, unstubbed probe has to work on the platform the suite runs on:
// Statfs on Linux, GetDiskFreeSpaceExW on Windows. If it silently failed, every
// space guard above would degrade to "not enforced" in production and nobody
// would notice until the partition filled.
func TestFreeSpaceBytesReportsRealVolume(t *testing.T) {
	free, err := freeSpaceBytes(t.TempDir())
	if err != nil {
		t.Fatalf("free space probe failed on this platform: %v", err)
	}
	if free == 0 {
		t.Fatal("free space probe reported 0 bytes on a writable temp volume")
	}
}

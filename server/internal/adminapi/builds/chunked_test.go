package builds

import (
	"bytes"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func adminHandlers(t *testing.T) (*Handlers, string) {
	t.Helper()
	root := t.TempDir()
	h := New(root)
	h.CurrentUser = func(*http.Request) string { return "admin" }
	return h, root
}

func initUpload(t *testing.T, h *Handlers, body string) (uploadID string, chunkSize int) {
	t.Helper()
	w := httptest.NewRecorder()
	h.UploadInit(w, httptest.NewRequest(http.MethodPost, "http://example.com/admin/api/upload/init",
		strings.NewReader(body)))
	if w.Code != http.StatusOK {
		t.Fatalf("init failed: %d %s", w.Code, w.Body.String())
	}
	var out struct {
		UploadID    string `json:"uploadId"`
		ChunkSize   int    `json:"chunkSize"`
		TotalChunks int    `json:"totalChunks"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	if out.UploadID == "" || out.ChunkSize <= 0 {
		t.Fatalf("init returned an unusable session: %s", w.Body.String())
	}
	return out.UploadID, out.ChunkSize
}

func putChunk(t *testing.T, h *Handlers, id string, idx int, data []byte) *httptest.ResponseRecorder {
	t.Helper()
	w := httptest.NewRecorder()
	h.UploadChunk(w, httptest.NewRequest(http.MethodPut,
		fmt.Sprintf("http://example.com/admin/api/upload/chunk?uploadId=%s&index=%d", id, idx),
		bytes.NewReader(data)))
	return w
}

func completeUpload(t *testing.T, h *Handlers, id string) *httptest.ResponseRecorder {
	t.Helper()
	w := httptest.NewRecorder()
	h.UploadComplete(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/upload/complete?uploadId="+id, nil))
	return w
}

func processUpload(t *testing.T, h *Handlers, id string) *httptest.ResponseRecorder {
	t.Helper()
	w := httptest.NewRecorder()
	h.UploadProcessStream(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/upload/process?uploadId="+id, nil))
	return w
}

// Multi-gigabyte builds can only be published through the chunked path, so this
// four-call sequence is how real releases actually ship. Nothing tested the
// sequence end to end: a chunk written at the wrong offset, or a part file
// renamed before the last chunk arrives, produces a corrupt archive that either
// fails to extract or — worse — extracts into a subtly wrong build.
func TestChunkedUploadPublishesTheReassembledArchive(t *testing.T) {
	h, root := adminHandlers(t)
	zipData := zipBytes(t, map[string]string{
		"ChillHub.exe":                   "launcher payload",
		"runtimes/win-x64/native/b3.dll": strings.Repeat("n", 3000),
		"данные/файл.dat":                "кириллица",
	})

	id, _ := initUpload(t, h, fmt.Sprintf(
		`{"kind":"game","gameId":"game","version":"1.0.0","zipName":"b.zip","totalSize":%d,"chunkSize":65536}`,
		len(zipData)))

	// 64 KiB is below the enforced minimum, so the server picks its own size;
	// read it back rather than assuming.
	m, err := h.readUploadMeta(id)
	if err != nil {
		t.Fatal(err)
	}
	for i := range m.TotalChunks {
		lo := i * m.ChunkSize
		hi := min(lo+m.ChunkSize, len(zipData))
		if w := putChunk(t, h, id, i, zipData[lo:hi]); w.Code != http.StatusOK {
			t.Fatalf("chunk %d: %d %s", i, w.Code, w.Body.String())
		}
	}
	if w := completeUpload(t, h, id); w.Code != http.StatusOK {
		t.Fatalf("complete: %d %s", w.Code, w.Body.String())
	}

	w := processUpload(t, h, id)
	events, garbage := ndjsonEvents(t, w.Body.String())
	if len(garbage) > 0 {
		t.Fatalf("plain text in the NDJSON stream: %q", garbage)
	}
	if hasErrorEvent(events) {
		t.Fatalf("process reported an error: %s", w.Body.String())
	}

	files := filepath.Join(root, "content", "game", "1.0.0", "files")
	for _, rel := range []string{"ChillHub.exe", "runtimes/win-x64/native/b3.dll", "данные/файл.dat"} {
		if _, err := os.Stat(filepath.Join(files, filepath.FromSlash(rel))); err != nil {
			t.Errorf("%q missing from the published build: %v", rel, err)
		}
	}
	b, err := os.ReadFile(filepath.Join(root, "manifests", "game", "1.0.0.json"))
	if err != nil {
		t.Fatalf("no manifest published: %v", err)
	}
	paths := manifestPaths(t, b)
	if !paths["ChillHub.exe"] || !paths["данные/файл.dat"] {
		t.Errorf("manifest does not describe the reassembled build: %v", paths)
	}
	// The archive is up to 30 GB; leaving it around after a successful publish
	// is what used to fill the content volume.
	if _, err := os.Stat(h.uploadZipPath(id)); err == nil {
		t.Error("the uploaded archive was kept after a successful publication")
	}
}

// The client sends the checksum it computed over the file it read. If the two
// disagree, some byte changed in transit and the archive must never be
// published: an undetected flipped bit becomes a wrong hash in the manifest, and
// every user then fails integrity checks against a build nobody can fix.
func TestChunkedUploadRejectsAChecksumMismatch(t *testing.T) {
	h, _ := adminHandlers(t)
	data := []byte("some archive bytes")
	id, _ := initUpload(t, h, fmt.Sprintf(
		`{"kind":"game","gameId":"game","version":"1.0.0","totalSize":%d,"expectedSha256":"%s"}`,
		len(data), strings.Repeat("ab", 32)))
	if w := putChunk(t, h, id, 0, data); w.Code != http.StatusOK {
		t.Fatalf("chunk: %d %s", w.Code, w.Body.String())
	}

	w := completeUpload(t, h, id)
	if w.Code != http.StatusBadRequest {
		t.Fatalf("a corrupted upload was accepted: %d %s", w.Code, w.Body.String())
	}
	if _, err := os.Stat(h.uploadZipPath(id)); err == nil {
		t.Error("the part file was promoted to a processable archive despite the mismatch")
	}
	m, err := h.readUploadMeta(id)
	if err != nil {
		t.Fatal(err)
	}
	if m.Status != "error" {
		t.Errorf("status %q: the admin UI cannot tell the session failed", m.Status)
	}
}

// A session missing a chunk must not complete. Silently accepting it publishes a
// build with a hole in the middle of the archive — which, with the right
// alignment, still unzips and produces a manifest describing truncated files.
func TestChunkedUploadRefusesToCompleteWithAMissingChunk(t *testing.T) {
	h, _ := adminHandlers(t)
	data := bytes.Repeat([]byte("x"), 3*64<<10)
	id, _ := initUpload(t, h, fmt.Sprintf(
		`{"kind":"game","gameId":"game","version":"1.0.0","totalSize":%d,"chunkSize":65536}`, len(data)))
	m, err := h.readUploadMeta(id)
	if err != nil {
		t.Fatal(err)
	}
	if m.TotalChunks < 2 {
		t.Fatalf("fixture produced %d chunk(s); the test needs several", m.TotalChunks)
	}
	// Everything but the first chunk.
	for i := 1; i < m.TotalChunks; i++ {
		lo, hi := i*m.ChunkSize, (i+1)*m.ChunkSize
		if hi > len(data) {
			hi = len(data)
		}
		if w := putChunk(t, h, id, i, data[lo:hi]); w.Code != http.StatusOK {
			t.Fatalf("chunk %d: %d %s", i, w.Code, w.Body.String())
		}
	}
	if w := completeUpload(t, h, id); w.Code != http.StatusBadRequest {
		t.Fatalf("an incomplete upload was accepted: %d %s", w.Code, w.Body.String())
	}
}

// A chunk shorter than the slot it claims means the client's read was cut off.
// Accepting it leaves the rest of the slot as the zeroes the part file was
// preallocated with, and the session then completes with a silently corrupted
// archive.
func TestChunkedUploadRejectsAShortChunk(t *testing.T) {
	h, _ := adminHandlers(t)
	id, _ := initUpload(t, h, `{"kind":"game","gameId":"game","version":"1.0.0","totalSize":200000,"chunkSize":65536}`)
	m, err := h.readUploadMeta(id)
	if err != nil {
		t.Fatal(err)
	}
	w := putChunk(t, h, id, 0, bytes.Repeat([]byte("y"), m.ChunkSize/2))
	if w.Code != http.StatusBadRequest {
		t.Fatalf("a short chunk was accepted: %d %s", w.Code, w.Body.String())
	}
	m2, err := h.readUploadMeta(id)
	if err != nil {
		t.Fatal(err)
	}
	if len(m2.Received) > 0 && m2.Received[0] {
		t.Error("the rejected chunk was marked as received; complete would then pass")
	}
}

// An index past the end of the session must be refused: the offset is computed
// as index*chunkSize and written into the preallocated part file, so an
// out-of-range index grows the file far past the declared size — an unbounded
// write on the content volume from a single request.
func TestChunkedUploadRejectsAnOutOfRangeIndex(t *testing.T) {
	h, _ := adminHandlers(t)
	id, _ := initUpload(t, h, `{"kind":"game","gameId":"game","version":"1.0.0","totalSize":1024}`)
	for _, idx := range []int{999999, -1} {
		w := putChunk(t, h, id, idx, []byte("x"))
		if w.Code != http.StatusBadRequest {
			t.Errorf("index %d accepted: %d %s", idx, w.Code, w.Body.String())
		}
	}
	st, err := os.Stat(h.uploadZipPartPath(id))
	if err != nil {
		t.Fatal(err)
	}
	if st.Size() != 1024 {
		t.Fatalf("part file grew to %d bytes from a rejected chunk", st.Size())
	}
}

// The status endpoint is what a resuming client asks which chunks it still owes.
// If it reported nothing, a resumed 30 GB upload would start again from zero.
func TestUploadStatusReportsReceivedChunks(t *testing.T) {
	h, _ := adminHandlers(t)
	data := bytes.Repeat([]byte("z"), 3*64<<10)
	id, _ := initUpload(t, h, fmt.Sprintf(
		`{"kind":"game","gameId":"game","version":"1.0.0","totalSize":%d,"chunkSize":65536}`, len(data)))
	m, err := h.readUploadMeta(id)
	if err != nil {
		t.Fatal(err)
	}
	if w := putChunk(t, h, id, 1, data[m.ChunkSize:2*m.ChunkSize]); w.Code != http.StatusOK {
		t.Fatalf("chunk: %d %s", w.Code, w.Body.String())
	}

	w := httptest.NewRecorder()
	h.UploadStatus(w, httptest.NewRequest(http.MethodGet,
		"http://example.com/admin/api/upload/status?uploadId="+id, nil))
	if w.Code != http.StatusOK {
		t.Fatalf("status: %d %s", w.Code, w.Body.String())
	}
	var out struct {
		Received    []int `json:"received"`
		TotalChunks int   `json:"totalChunks"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &out); err != nil {
		t.Fatal(err)
	}
	if len(out.Received) != 1 || out.Received[0] != 1 {
		t.Fatalf("received = %v, want [1]", out.Received)
	}
	if out.TotalChunks != m.TotalChunks {
		t.Fatalf("totalChunks = %d, want %d", out.TotalChunks, m.TotalChunks)
	}
}

// The chunked path builds filesystem paths from ids stored in the session, so a
// session must never be created with an id the path guards would reject. Letting
// init through and relying on the later handlers to notice would leave the
// dangerous value sitting in meta.json.
func TestUploadInitRejectsUnsafeIdentifiers(t *testing.T) {
	h, root := adminHandlers(t)
	for name, body := range map[string]string{
		"traversalGameID": `{"kind":"game","gameId":"../evil","version":"1.0.0","totalSize":10}`,
		"traversalVer":    `{"kind":"game","gameId":"game","version":"../../x","totalSize":10}`,
		"zeroSize":        `{"kind":"game","gameId":"game","version":"1.0.0","totalSize":0}`,
		"negativeSize":    `{"kind":"game","gameId":"game","version":"1.0.0","totalSize":-1}`,
		"notJSON":         `nonsense`,
	} {
		t.Run(name, func(t *testing.T) {
			w := httptest.NewRecorder()
			h.UploadInit(w, httptest.NewRequest(http.MethodPost,
				"http://example.com/admin/api/upload/init", strings.NewReader(body)))
			if w.Code != http.StatusBadRequest {
				t.Fatalf("accepted: %d %s", w.Code, w.Body.String())
			}
		})
	}
	if entries, err := os.ReadDir(h.uploadBaseDir()); err == nil && len(entries) > 0 {
		t.Errorf("rejected sessions still allocated storage: %v", entries)
	}
	if _, err := os.Stat(filepath.Join(root, "content", "..", "evil")); err == nil {
		t.Error("a traversing gameId created a directory outside the content root")
	}
}

// kind=launcher means the launcher publishes itself; the gameId is then not the
// client's to choose. Accepting one would publish a launcher build under a
// game's manifest directory, where no launcher would ever look for it.
func TestUploadInitForcesTheLauncherGameID(t *testing.T) {
	h, _ := adminHandlers(t)
	id, _ := initUpload(t, h, `{"kind":"launcher","gameId":"something-else","version":"1.1.9","totalSize":10}`)
	m, err := h.readUploadMeta(id)
	if err != nil {
		t.Fatal(err)
	}
	if m.GameID != LauncherGameID {
		t.Fatalf("gameId = %q, want %q", m.GameID, LauncherGameID)
	}
}

// Every chunked endpoint runs behind an nginx location that bypasses
// auth_request, so the only authentication these handlers get is their own. A
// gap here is an unauthenticated write into the content root.
func TestChunkedEndpointsRequireAuth(t *testing.T) {
	root := t.TempDir()
	h := New(root) // no CurrentUser: nothing is authorised
	calls := map[string]func(http.ResponseWriter, *http.Request){
		"init":     h.UploadInit,
		"chunk":    h.UploadChunk,
		"status":   h.UploadStatus,
		"complete": h.UploadComplete,
		"process":  h.UploadProcessStream,
		"cleanup":  h.UploadCleanup,
		"stream":   h.UploadStream,
	}
	for name, fn := range calls {
		w := httptest.NewRecorder()
		req := httptest.NewRequest(http.MethodPost, "http://example.com/x?uploadId="+strings.Repeat("a", 32)+"&index=0", strings.NewReader("{}"))
		fn(w, req)
		if w.Code != http.StatusUnauthorized {
			t.Errorf("%s answered %d to an unauthenticated request", name, w.Code)
		}
	}
}

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
	"strings"
	"testing"
)

// ОДИН ПУТЬ ПУБЛИКАЦИИ — ОДИН СПОСОБ ЕГО ПРОВЕРИТЬ.
//
// Половина проверок этого пакета публиковала сборку простым обработчиком
// /admin/api/upload, а панель публикует кусками. То есть защита от zip-бомбы,
// от выхода за каталог, разбор имён и сборка манифеста проверялись НЕ НА ТОМ
// коде, который работает у оператора: совпадение держалось на том, что оба
// пути звали одни и те же внутренние функции.
//
// publishZip проводит архив тем же четырёхшаговым путём, каким его проводит
// панель: init → куски → complete → process.
//
// ОТКАЗ ПОСЛЕ НАЧАЛА ПОТОКА — ЭТО СОБЫТИЕ, А НЕ КОД ОТВЕТА. Заголовки уже
// ушли, и http.Error в этот момент только подмешал бы текст в тело NDJSON.
// Поэтому событие "error" здесь превращается в 400: для проверяющего это тот
// же «не опубликовалось», а разница в способе доставки к делу не относится.
func publishZip(t *testing.T, h *Handlers, kind, gid, ver string, zipData []byte) (int, string) {
	t.Helper()

	// Чанковые ручки проверяют сессию сами: nginx их не прикрывает.
	if h.CurrentUser == nil {
		h.CurrentUser = func(*http.Request) string { return "admin" }
	}

	body, err := json.Marshal(map[string]any{
		"kind": kind, "gameId": gid, "version": ver,
		"zipName": "build.zip", "totalSize": len(zipData),
	})
	if err != nil {
		t.Fatal(err)
	}

	w := httptest.NewRecorder()
	h.UploadInit(w, httptest.NewRequest(http.MethodPost,
		"http://example.com/admin/api/upload/init", strings.NewReader(string(body))))
	if w.Code != http.StatusOK {
		return w.Code, w.Body.String()
	}
	var started struct {
		UploadID  string `json:"uploadId"`
		ChunkSize int    `json:"chunkSize"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &started); err != nil {
		t.Fatalf("init вернул не JSON: %s", w.Body.String())
	}

	for i, off := 0, 0; off < len(zipData); i, off = i+1, off+started.ChunkSize {
		end := min(off+started.ChunkSize, len(zipData))
		if cw := putChunk(t, h, started.UploadID, i, zipData[off:end]); cw.Code != http.StatusOK {
			return cw.Code, cw.Body.String()
		}
	}
	// Пустой архив не даёт ни одного куска, а сессию всё равно надо закрыть.
	if len(zipData) == 0 {
		if cw := putChunk(t, h, started.UploadID, 0, nil); cw.Code != http.StatusOK {
			return cw.Code, cw.Body.String()
		}
	}

	if cw := completeUpload(t, h, started.UploadID); cw.Code != http.StatusOK {
		return cw.Code, cw.Body.String()
	}

	pw := processUpload(t, h, started.UploadID)
	if pw.Code != http.StatusOK {
		return pw.Code, pw.Body.String()
	}
	if msg, bad := streamFailure(pw.Body.String()); bad {
		return http.StatusBadRequest, msg
	}
	return http.StatusOK, pw.Body.String()
}

// publishInto проводит архив чанковым путём и КЛАДЁТ ИСХОД В ТОТ ЖЕ РЕКОРДЕР,
// в каком его ждали проверки простого обработчика: код ответа и тело.
//
// Так проверка остаётся про сборку: ей всё равно, каким путём архив доехал, —
// важно, что опубликовалось, что отвергнуто и что осталось на диске.
//
// На успехе телом кладётся манифест: простой обработчик отдавал его ответом, и
// половина проверок читает именно его. Чанковый путь отдаёт поток событий, а
// манифест пишет на диск — оттуда и берём.
func publishInto(t *testing.T, h *Handlers, w *httptest.ResponseRecorder, kind, gid, ver string, zipData []byte) {
	t.Helper()
	code, body := publishZip(t, h, kind, gid, ver, zipData)
	w.Code = code
	w.Body.Reset()
	if code == http.StatusOK {
		if b, err := os.ReadFile(filepath.Join(h.root, "manifests", gid, ver+".json")); err == nil {
			w.Body.Write(b)
			return
		}
	}
	w.Body.WriteString(body)
}

// publishAndActivate публикует сборку и делает её той, что получают игроки.
//
// Чанковый путь НЕ двигает latest.json сам, и это решение, а не упущение:
// «залито» и «отдано игрокам» — два разных события, между которыми стоит
// человек. Прежний обработчик умел совмещать их полем updateLatest; здесь
// совмещение собирается из тех же двух шагов, какими его собирает панель.
func publishAndActivate(t *testing.T, h *Handlers, w *httptest.ResponseRecorder, gid, ver string, zipData []byte) {
	t.Helper()
	publishInto(t, h, w, "game", gid, ver, zipData)
	if w.Code != http.StatusOK {
		return
	}
	if err := h.ActivateVersion(NamespaceGame, gid, ver); err != nil {
		t.Fatalf("версия %s/%s не стала активной: %v", gid, ver, err)
	}
}

// streamFailure ищет в потоке событий отказ и возвращает его текст.
func streamFailure(stream string) (string, bool) {
	for line := range strings.SplitSeq(stream, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		var ev struct {
			Type    string `json:"type"`
			Message string `json:"message"`
			Error   string `json:"error"`
		}
		if json.Unmarshal([]byte(line), &ev) != nil {
			continue
		}
		if ev.Type == "error" {
			if ev.Message != "" {
				return ev.Message, true
			}
			return ev.Error, true
		}
	}
	return "", false
}

// assertNoPublishScratch fails if the publish left rubbish behind.
//
// Каталог сессии загрузки (<root>/tmp/uploads/<id>) сюда НЕ считается: он
// живёт до «отменить» или до уборки по расписанию, и в этом весь смысл
// докачки — оборванная заливка обязана пережить обрыв. А вот распакованное
// на полпути дерево и файлы разбора остаться не имеют права: том общий с
// публичным API и тремя другими сайтами.
func assertNoPublishScratch(t *testing.T, root string) {
	t.Helper()
	entries, err := os.ReadDir(filepath.Join(root, "tmp"))
	if err != nil {
		return // не создавали — значит, пусто
	}
	for _, e := range entries {
		if e.IsDir() && e.Name() == "uploads" {
			continue
		}
		t.Fatalf("после публикации в tmp осталось: %s", e.Name())
	}
}

// assertNoStagingLeftovers fails if a half-promoted version directory survived.
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

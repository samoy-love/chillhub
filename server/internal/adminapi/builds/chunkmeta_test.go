package builds

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ЧАНК СЧИТАЕТСЯ ДОСТАВЛЕННЫМ ПО ОТМЕТКЕ В meta.json, А НЕ ПО ЗАПИСИ БАЙТОВ.
//
// Ошибка записи meta.json — том кончился ровно на том, чем занята
// многогигабайтная заливка, — отбрасывалась, и ответ был «ok». Клиент к этому
// чанку больше не возвращался, а UploadComplete отвечал «missing chunk N» про
// чанк, который физически лежит в part-файле: повторный complete это не чинит
// никогда, и связать отказ с единственной строкой в журнале нечем.
//
// Отказ записи здесь наводится через сам meta.json: uploadId внутри него
// указывает на путь, по которому уже лежит обычный файл, поэтому MkdirAll в
// writeUploadMeta не проходит. Способ синтетический, а вот отказ — тот самый,
// который надо увидеть в ответе.
func TestUploadChunkRefusesWhenTheReceiptCannotBeStored(t *testing.T) {
	h, root := adminHandlers(t)
	id, chunkSize := initUpload(t, h,
		`{"kind":"game","gameId":"game","version":"1.0.0","fileName":"build.zip","totalSize":10}`)

	metaPath := filepath.Join(root, "tmp", "uploads", id, "meta.json")
	b, err := os.ReadFile(metaPath)
	if err != nil {
		t.Fatal(err)
	}
	var m uploadMeta
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatal(err)
	}
	// Свободный, но уже занятый обычным файлом каталог загрузки.
	const blocked = "00112233445566778899aabbccddeeff"
	mustWriteFile(t, filepath.Join(root, "tmp", "uploads", blocked), "не каталог")
	m.UploadID = blocked
	out, err := json.Marshal(&m)
	if err != nil {
		t.Fatal(err)
	}
	mustWriteFile(t, metaPath, string(out))

	w := putChunk(t, h, id, 0, []byte(strings.Repeat("x", min(chunkSize, 10))))

	if w.Code == http.StatusOK {
		t.Fatalf("чанк без записанной отметки объявлен доставленным: %s", w.Body.String())
	}
}

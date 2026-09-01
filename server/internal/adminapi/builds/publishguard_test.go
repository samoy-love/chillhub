package builds

import (
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// readPublishedManifest returns the stored manifest of a published version.
func readPublishedManifest(t *testing.T, root, gid, ver string) manifest {
	t.Helper()
	b, err := os.ReadFile(filepath.Join(root, "manifests", gid, ver+".json"))
	if err != nil {
		t.Fatalf("manifest %s/%s: %v", gid, ver, err)
	}
	var m manifest
	if err := json.Unmarshal(b, &m); err != nil {
		t.Fatalf("manifest %s/%s is not JSON: %v", gid, ver, err)
	}
	return m
}

// ОТКАЗ ВАЛИДАЦИИ НЕ ДОЛЖЕН ЗАСТАВАТЬ ДЕРЕВО УЖЕ ПОДМЕНЁННЫМ.
//
// Перезалить игру под тем же номером версии — штатный способ починить сборку.
// Проверка манифеста жила внутри его записи, то есть срабатывала уже после
// промоута: прежнее дерево к этому моменту удалено, откатывать нечего,
// и на диске оставались НОВЫЕ файлы под СТАРЫМ манифестом. Клиент не сходится
// по blake3 сразу на всех файлах и либо качает их без конца, либо объявляет
// установку битой, а в панели версия выглядит целой.
func TestUploadRefusedByValidationLeavesThePublishedVersionIntact(t *testing.T) {
	root := t.TempDir()
	h := New(root)

	w := httptest.NewRecorder()
	h.Upload(w, uploadRequest(t, "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "первая сборка"})))
	if w.Code != http.StatusOK {
		t.Fatalf("первая заливка не прошла: %d %s", w.Code, w.Body.String())
	}

	// Пробел в начале имени файла: Windows молча съедает такие имена по-своему,
	// поэтому pathProblem их отвергает — «a.txt» и « a.txt» иначе один файл, но
	// две записи манифеста.
	w = httptest.NewRecorder()
	h.Upload(w, uploadRequest(t, "game", "1.0.0", zipBytes(t, map[string]string{
		"a.txt":       "вторая сборка",
		"sub/ bad.md": "x",
	})))
	if w.Code == http.StatusOK {
		t.Fatalf("сборка с непубликуемым путём принята: %s", w.Body.String())
	}

	// Диск обязан остаться таким, каким его описывает опубликованный манифест.
	published := filepath.Join(root, "content", "game", "1.0.0", "files")
	body, err := os.ReadFile(filepath.Join(published, "a.txt"))
	if err != nil {
		t.Fatalf("опубликованный файл пропал: %v", err)
	}
	if string(body) != "первая сборка" {
		t.Fatalf("на диске файл из отвергнутой сборки: %q", string(body))
	}
	if _, err := os.Stat(filepath.Join(published, "sub")); !os.IsNotExist(err) {
		t.Fatalf("файлы отвергнутой сборки опубликованы: %v", err)
	}
	if m := readPublishedManifest(t, root, "game", "1.0.0"); len(m.Files) != 1 {
		t.Fatalf("манифест описывает %d файлов, а опубликован один", len(m.Files))
	}
	assertNoStagingLeftovers(t, filepath.Join(root, "content", "game"))
}

// Удаление версии обязано брать тот же замок, что и публикация.
//
// Без него удаление успевает пройти между промоутом и записью манифеста
// параллельной публикации: манифест публикуется, а файлов под ним уже нет —
// каждый клиент получает 404 на каждый файл, а панель показывает версию как
// существующую.
func TestRemoveVersionWaitsForAPublicationOfTheSameVersion(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedManifest(t, h, "game", "1.0.0", true)
	seedVersionTree(t, root, "game", "1.0.0")

	unlock := lockPublish("game", "1.0.0")
	done := make(chan error, 1)
	go func() { done <- h.removeVersion("game", "1.0.0") }()

	select {
	case <-done:
		unlock()
		t.Fatal("удаление прошло, пока версия публикуется: манифест останется без файлов")
	case <-time.After(100 * time.Millisecond):
	}

	unlock()
	if err := <-done; err != nil {
		t.Fatalf("удаление после снятия замка: %v", err)
	}
}

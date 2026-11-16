package builds

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestOverlappingModBuildKeepsItsSourceWithItsFiles(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	const gid, version = "demo", "1.0"
	stage := func(label string) *StagedTree {
		t.Helper()
		s, err := h.StageTree(NamespaceMods, gid, version)
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(s.Discard)
		if err := os.WriteFile(filepath.Join(s.FilesRoot, "pack.txt"), []byte(label), 0o600); err != nil {
			t.Fatal(err)
		}
		return s
	}
	slow, fast := stage("slow"), stage("fast")
	slowSource, release := make(chan struct{}), make(chan struct{})
	done := make(chan error, 1)
	go func() {
		_, err := h.PublishModpack(slow, nil, func(r PublishResult) ([]byte, error) {
			close(slowSource)
			<-release
			return []byte(fmt.Sprintf(`{"label":"slow","treeDigest":%q}`, r.TreeDigest)), nil
		})
		done <- err
	}()
	select {
	case <-slowSource:
	case err := <-done:
		t.Fatalf("slow build never reached the source stage: %v", err)
	}
	_, fastErr := h.PublishModpack(fast, nil, func(r PublishResult) ([]byte, error) {
		return []byte(fmt.Sprintf(`{"label":"fast","treeDigest":%q}`, r.TreeDigest)), nil
	})
	close(release)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	if fastErr != nil {
		t.Fatal(fastErr)
	}
	data, err := ReadModSource(root, gid, version)
	if err != nil {
		t.Fatal(err)
	}
	var source struct {
		Label      string `json:"label"`
		TreeDigest string `json:"treeDigest"`
	}
	if err := json.Unmarshal(data, &source); err != nil {
		t.Fatal(err)
	}
	filesDir := filepath.Join(h.ContentDirFor(NamespaceMods, gid), version, "files")
	payload, err := os.ReadFile(filepath.Join(filesDir, "pack.txt"))
	if err != nil {
		t.Fatal(err)
	}
	files, _, err := walkManifest(filesDir, nil)
	if err != nil {
		t.Fatal(err)
	}
	if source.Label != string(payload) || source.TreeDigest != treeDigest(files) {
		t.Fatalf("source %+v does not match published files %q", source, payload)
	}
}

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
	publishInto(t, h, w, "game", "game", "1.0.0", zipBytes(t, map[string]string{"a.txt": "первая сборка"}))
	if w.Code != http.StatusOK {
		t.Fatalf("первая заливка не прошла: %d %s", w.Code, w.Body.String())
	}

	// Пробел в начале имени файла: Windows молча съедает такие имена по-своему,
	// поэтому pathProblem их отвергает — «a.txt» и « a.txt» иначе один файл, но
	// две записи манифеста.
	w = httptest.NewRecorder()
	publishInto(t, h, w, "game", "game", "1.0.0", zipBytes(t, map[string]string{
		"a.txt":       "вторая сборка",
		"sub/ bad.md": "x",
	}))
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

	unlock := lockPublish(h.manifestsDir("game"))
	done := make(chan int, 1)
	go func() {
		w := httptest.NewRecorder()
		r := httptest.NewRequestWithContext(t.Context(), http.MethodPost,
			"/admin/api/deleteVersion?gameId=game&version=1.0.0", nil)
		h.DeleteVersion(w, r)
		done <- w.Code
	}()

	select {
	case status := <-done:
		unlock()
		t.Fatalf("удаление завершилось, пока версия публикуется: %d", status)
	case <-time.After(100 * time.Millisecond):
	}

	unlock()
	if status := <-done; status != http.StatusOK {
		t.Fatalf("удаление после снятия замка: %d", status)
	}
}

func TestModDeletionChecksActiveVersionUnderPublicationLock(t *testing.T) {
	h := New(t.TempDir())
	dir := h.ManifestsDirFor(NamespaceMods, "demo")
	if err := os.MkdirAll(dir, 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "1.0.json"), []byte("{}"), 0o600); err != nil {
		t.Fatal(err)
	}

	unlock := lockPublish(dir)
	done := make(chan error, 1)
	go func() { done <- h.DeletePublished(NamespaceMods, "demo", "1.0") }()
	if err := writeLatestJSON(dir, "1.0"); err != nil {
		unlock()
		t.Fatal(err)
	}
	unlock()
	if err := <-done; err == nil {
		t.Fatal("deleted an active mod version")
	}
	if _, err := os.Stat(filepath.Join(dir, "1.0.json")); err != nil {
		t.Fatalf("active manifest missing: %v", err)
	}
}

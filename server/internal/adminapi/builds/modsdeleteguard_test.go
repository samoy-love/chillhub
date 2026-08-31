package builds

import (
	"os"
	"path/filepath"
	"testing"
)

// seedModpackVersion lays out one published modpack version for a game.
func seedModpackVersion(t *testing.T, root, gid, ver string) {
	t.Helper()
	mustMkdirAll(t, filepath.Join(root, "manifests", "_mods", gid))
	mustWriteFile(t, filepath.Join(root, "manifests", "_mods", gid, ver+".json"), "{}")
	mustMkdirAll(t, filepath.Join(root, "content", "_mods", gid, ver, "files"))
	mustWriteFile(t, filepath.Join(root, "content", "_mods", gid, ver, "files", "mod.dll"), "payload")
}

// Модпаки живут в content/_mods/{игра}/{версия}, а проверка «внутри ли пути»
// делалась от content — на два уровня выше удаляемого. Версия «..» схлопывала
// путь ровно в content/_mods, оставаясь «внутри», и os.RemoveAll сносил ВСЕ
// собранные версии ВСЕХ игр; версия «.» тем же способом сносила все версии
// одной игры. Ручка отвечала 200 {"status":"ok"}, а манифесты оставались на
// месте — панель продолжала показывать версии, у которых на диске нет файлов.
func TestDeletePublishedCannotReachAboveTheVersionDirectory(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedModpackVersion(t, root, "game", "1.0.0")
	seedModpackVersion(t, root, "other", "2.0.0")

	for _, ver := range []string{"..", ".", "1.0.0/.."} {
		if err := h.DeletePublished(NamespaceMods, "game", ver); err == nil {
			t.Errorf("версия %q удалена без возражений", ver)
		}
	}

	for _, gid := range []string{"game", "other"} {
		if _, err := os.Stat(filepath.Join(root, "content", "_mods", gid)); err != nil {
			t.Fatalf("дерево модпаков игры %q снесено: %v", gid, err)
		}
	}
	if _, err := os.Stat(filepath.Join(root, "content", "_mods", "game", "1.0.0", "files", "mod.dll")); err != nil {
		t.Fatalf("файлы версии пропали: %v", err)
	}
}

// Обычное удаление обязано продолжать работать: проверка выше должна отсекать
// путь, равный каталогу-родителю, а не всё подряд.
func TestDeletePublishedStillRemovesARealVersion(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedModpackVersion(t, root, "game", "1.0.0")
	seedModpackVersion(t, root, "game", "2.0.0")

	if err := h.DeletePublished(NamespaceMods, "game", "1.0.0"); err != nil {
		t.Fatalf("удаление настоящей версии: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "content", "_mods", "game", "1.0.0")); !os.IsNotExist(err) {
		t.Fatalf("версия осталась на диске: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "manifests", "_mods", "game", "1.0.0.json")); !os.IsNotExist(err) {
		t.Fatalf("манифест версии остался: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "content", "_mods", "game", "2.0.0", "files", "mod.dll")); err != nil {
		t.Fatalf("соседняя версия пострадала: %v", err)
	}
}

// removeVersion получает метку версии не только из запроса, но и из имени файла
// манифеста, поэтому «.» до него дойти может. Раньше путь content/{игра}/.
// схлопывался в каталог игры, проверка «внутри» его пропускала как равный базе,
// и os.RemoveAll уносил все версии игры разом.
func TestRemoveVersionRefusesTheGameDirectoryItself(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	seedVersionTree(t, root, "game", "1.0.0")

	if err := h.removeVersion("game", "."); err == nil {
		t.Fatal("removeVersion принял версию, которая указывает на каталог игры")
	}
	if _, err := os.Stat(filepath.Join(root, "content", "game", "1.0.0", "files", "app.exe")); err != nil {
		t.Fatalf("версии игры удалены: %v", err)
	}
}

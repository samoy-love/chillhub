package games

import (
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// СЛУЖЕБНЫЕ ИМЕНА — НЕ ИГРЫ. Под manifests/ лежат два каталога, которые
// удалять нельзя ни при каких обстоятельствах: «_registry» с games.json —
// реестром всех игр — и «_mods» со всеми собранными модпаками. IsSafeGameID
// пропускает оба (подчёркивание — законный символ), внутрь своих корней они
// попадают, и дальше по пути возразить было некому: реестр стирался, ответ был
// «ok», а лаунчер получал пустой список игр. Save эти имена отвергает с самого
// начала — удаление обязано делать то же самое.
func TestPurgeRefusesTheReservedDirectories(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	saveRegistry(t, h, []Entry{{GameID: "raft", Title: "Рафт"}})

	modpacks := filepath.Join(root, "content", "_mods", "raft", "1.0.0")
	if err := os.MkdirAll(modpacks, 0o750); err != nil {
		t.Fatal(err)
	}

	for _, gid := range []string{"_registry", "_mods", "_REGISTRY"} {
		w := httptest.NewRecorder()
		h.Purge(w, formPost(t, "/admin/api/games/purge", "gameId="+gid))
		if w.Code != http.StatusBadRequest {
			t.Errorf("purge принял служебный gameId %q: %d %s", gid, w.Code, w.Body.String())
		}
	}

	if _, err := os.Stat(h.registryPath()); err != nil {
		t.Fatalf("реестр игр удалён: %v", err)
	}
	if _, err := os.Stat(modpacks); err != nil {
		t.Fatalf("дерево модпаков удалено: %v", err)
	}
	if got := decodeItems(t, h.Get, httptest.NewRequestWithContext(t.Context(), http.MethodGet, "/admin/api/games", nil)); len(got) != 1 {
		t.Fatalf("реестр после отказов: %+v", got)
	}
}

// obstructRemoval makes os.RemoveAll of dir fail, or skips the test when
// neither obstruction bites on this platform. Same two rehearsed obstructions
// the builds package uses: a parent without the write bit (POSIX) and an open
// handle on a file inside the tree (Windows).
func obstructRemoval(t *testing.T, dir string) {
	t.Helper()

	seed := func(base string) string {
		d := filepath.Join(base, "tree", "sub")
		if err := os.MkdirAll(d, 0o750); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(d, "file.bin"), []byte("x"), 0o600); err != nil {
			t.Fatal(err)
		}
		return filepath.Join(base, "tree")
	}

	probe := seed(t.TempDir())
	if os.Chmod(filepath.Dir(probe), 0o500) == nil {
		err := os.RemoveAll(probe)
		_ = os.Chmod(filepath.Dir(probe), 0o755)
		if err != nil {
			parent := filepath.Dir(dir)
			if err := os.Chmod(parent, 0o500); err != nil {
				t.Fatalf("chmod %s: %v", parent, err)
			}
			t.Cleanup(func() { _ = os.Chmod(parent, 0o755) })
			return
		}
	}

	probe2 := seed(t.TempDir())
	pf, err := os.Open(filepath.Join(probe2, "sub", "file.bin"))
	if err != nil {
		t.Fatalf("open probe file: %v", err)
	}
	rmErr := os.RemoveAll(probe2)
	_ = pf.Close()
	if rmErr != nil {
		f, err := os.Open(filepath.Join(dir, "sub", "file.bin"))
		if err != nil {
			t.Fatalf("open payload: %v", err)
		}
		t.Cleanup(func() { _ = f.Close() })
		return
	}

	t.Skip("на этой платформе os.RemoveAll не удаётся заставить упасть")
}

// ЧТО ОСТАЛОСЬ НА ДИСКЕ — ЭТО НЕ УСПЕХ. Ошибки удаления только писались в
// журнал, ответ был 200, а панель печатала «удалена вместе с манифестами и
// сборками». Строки в реестре к этому моменту уже нет, поэтому застрявшее
// дерево не видно ни в панели, ни в списке игр — зато оно занимает место,
// и показатель свободного места расходится с ожиданием без объяснений.
func TestPurgeReportsTreesItCouldNotRemove(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	saveRegistry(t, h, []Entry{{GameID: "raft", Title: "Рафт"}})

	conDir := filepath.Join(root, "content", "raft")
	if err := os.MkdirAll(filepath.Join(conDir, "sub"), 0o750); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(conDir, "sub", "file.bin"), []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	obstructRemoval(t, conDir)

	w := httptest.NewRecorder()
	h.Purge(w, formPost(t, "/admin/api/games/purge", "gameId=raft"))

	if w.Code == http.StatusOK {
		t.Fatalf("удаление отчиталось об успехе, оставив сборки на диске: %s", w.Body.String())
	}
	if !strings.Contains(w.Body.String(), "content") {
		t.Errorf("в ответе не сказано, что именно осталось: %q", w.Body.String())
	}
	if _, err := os.Stat(conDir); err != nil {
		t.Fatalf("помеха не сработала, дерево удалено: %v", err)
	}
}

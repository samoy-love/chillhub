package mods

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"ChillHub/server/internal/adminapi/builds"
)

// СВОДКА ОТВЕЧАЕТ ПРО КАЖДУЮ ИГРУ С МОДАМИ, А НЕ ТОЛЬКО ПРО ОТСТАВШИЕ.
//
// Пока сюда попадали одни отставшие, «всё свежо» и «проверка не сработала»
// выглядели одинаково — пустым списком. Отличить их можно было только чтением
// логов сервера, а панель в обоих случаях молчала.
func TestModsSummaryReportsEveryGameWithMods(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.setLatest("Team", "Pack", "1.0.0", false)
	h, root := testHandlers(t, fs)
	publishPack(t, root, "lethal-company", "Team-Pack-1.0.0")

	rows := h.modsSummary(context.Background())

	if len(rows) != 1 {
		t.Fatalf("строк в сводке %d, ждали одну: %+v", len(rows), rows)
	}
	got := rows[0]
	if got.GameID != "lethal-company" || got.Active != "Team-Pack-1.0.0" {
		t.Errorf("не та игра или не та версия: %+v", got)
	}
	if got.Latest != "1.0.0" {
		t.Errorf("последняя версия с Thunderstore = %q, ждали 1.0.0", got.Latest)
	}
	if got.Behind || got.Deprecated || got.Error != "" {
		t.Errorf("свежая игра объявлена требующей внимания: %+v", got)
	}
	if got.Pack != "Team/Pack" {
		t.Errorf("пакет назван как %q", got.Pack)
	}
}

// Вышла новая версия — строка та же, но теперь она задача.
func TestModsSummaryMarksOutdatedPack(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.setLatest("Team", "Pack", "1.1.0", false)
	h, root := testHandlers(t, fs)
	publishPack(t, root, "lethal-company", "Team-Pack-1.0.0")

	rows := h.modsSummary(context.Background())

	if len(rows) != 1 || !rows[0].Behind {
		t.Fatalf("отставшая сборка не отмечена: %+v", rows)
	}
	if rows[0].Latest != "1.1.0" {
		t.Errorf("в строке не та новая версия: %+v", rows[0])
	}
}

// Устаревший пакет — тоже повод, даже когда версия совпадает: пересобирать
// нечего, решать, чем заменить, придётся человеку.
func TestModsSummaryMarksDeprecatedPack(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.setLatest("Team", "Pack", "1.0.0", true)
	h, root := testHandlers(t, fs)
	publishPack(t, root, "lethal-company", "Team-Pack-1.0.0")

	rows := h.modsSummary(context.Background())

	if len(rows) != 1 || rows[0].Behind {
		t.Fatalf("совпадающая версия объявлена отставшей: %+v", rows)
	}
	if !rows[0].Deprecated {
		t.Errorf("устаревший пакет не отмечен: %+v", rows[0])
	}
}

// НЕИЗВЕСТНОЕ СОСТОЯНИЕ НАЗЫВАЕТСЯ СВОИМ ИМЕНЕМ. Thunderstore не ответил про
// пакет — это не «всё свежо»: строка остаётся, но с причиной, и задачей не
// считается.
func TestModsSummaryTellsWhenCheckFailed(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	// Про пакет фейк ничего не знает: setLatest не звали.
	h, root := testHandlers(t, fs)
	publishPack(t, root, "lethal-company", "Team-Pack-1.0.0")

	rows := h.modsSummary(context.Background())

	if len(rows) != 1 {
		t.Fatalf("строк %d, ждали одну: %+v", len(rows), rows)
	}
	if rows[0].Error == "" {
		t.Errorf("провалившаяся проверка выдана за успешную: %+v", rows[0])
	}
	if rows[0].Behind || rows[0].Deprecated {
		t.Errorf("неизвестное состояние засчитано как задача: %+v", rows[0])
	}
}

// Игра с выключенными модами и игра без собранного пака в сводке не появляются:
// решать по ним нечего.
func TestModsSummarySkipsGamesWithoutBuiltPack(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.setLatest("Team", "Pack", "1.0.0", false)
	h, _ := testHandlers(t, fs)

	rows := h.modsSummary(context.Background())

	if rows == nil {
		t.Fatal("пустая сводка обязана быть пустым срезом, а не nil: в JSON это [] против null")
	}
	if len(rows) != 0 {
		t.Errorf("в сводку попали игры без собранного модпака: %+v", rows)
	}
}

// Задачи считаются по строкам, требующим внимания, а не по их количеству:
// иначе значок горел бы на каждой игре с модами, то есть всегда.
func TestSummaryCountsOnlyPending(t *testing.T) {
	fs := newFakeStore(t)
	seedPack(fs)
	fs.setLatest("Team", "Pack", "1.0.0", false)
	h, root := testHandlers(t, fs)
	publishPack(t, root, "lethal-company", "Team-Pack-1.0.0")

	if got := h.summary(context.Background(), true); got.Pending != 0 {
		t.Errorf("свежая игра даёт %d задач: %+v", got.Pending, got.Mods)
	}

	fs.setLatest("Team", "Pack", "1.1.0", false)
	if got := h.summary(context.Background(), true); got.Pending != 1 {
		t.Errorf("отставшая игра дала %d задач: %+v", got.Pending, got.Mods)
	}
}

// ПОЛОВИНА ПРО ЛАУНЧЕР НЕ КЕШИРУЕТСЯ. Она читает два файла с диска, а держали
// её десять минут вместе с сетевой половиной: сборку загрузили, а панель ещё
// треть часа показывала прежнюю самую свежую версию.
func TestSummaryLauncherHalfIgnoresCache(t *testing.T) {
	fs := newFakeStore(t)
	h, root := testHandlers(t, fs)
	dir := filepath.Join(root, "manifests", "launcher")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	writeJSON(t, filepath.Join(dir, "1.6.5.json"), map[string]any{"version": "1.6.5"})
	writeJSON(t, filepath.Join(dir, "latest.json"), map[string]string{"version": "1.6.5"})

	if got := h.summary(context.Background(), false); got.Launcher.Pending {
		t.Fatalf("задача на пустом месте: %+v", got.Launcher)
	}

	// Загрузили новую сборку — ответ обязан измениться сразу, без force.
	writeJSON(t, filepath.Join(dir, "1.6.6.json"), map[string]any{"version": "1.6.6"})

	got := h.summary(context.Background(), false)
	if got.Launcher.Newest != "1.6.6" || !got.Launcher.Pending {
		t.Errorf("свежая сборка не видна из-за кеша: %+v", got.Launcher)
	}
}

// publishPack кладёт на диск то, что оставляет после себя сборка модпака:
// манифест версии, отметку активной и запись об источнике.
func publishPack(t *testing.T, root, gid, version string) {
	t.Helper()
	dir := filepath.Join(root, "manifests", string(builds.NamespaceMods), gid)
	if err := os.MkdirAll(filepath.Join(dir, "sources"), 0o755); err != nil {
		t.Fatal(err)
	}

	writeJSON(t, filepath.Join(dir, version+".json"), map[string]any{
		"version": version, "gameId": gid, "files": []any{},
	})
	writeJSON(t, filepath.Join(dir, "latest.json"), map[string]string{"version": version})
	writeJSON(t, filepath.Join(dir, "sources", version+".json"), Source{
		Kind:    SourceThunderstore,
		Version: version,
		BuiltAt: time.Now().UTC().Format(time.RFC3339),
	})
}

func writeJSON(t *testing.T, path string, value any) {
	t.Helper()
	data, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
}

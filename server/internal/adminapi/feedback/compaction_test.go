package feedback

import (
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"ChillHub/server/internal/adminutil"
)

// fillJournal кладёт в журнал столько строк, чтобы он перевалил порог
// компакции, минуя публичную ручку: тест про порог, а не про приём отчётов.
func fillJournal(t *testing.T, root string) {
	t.Helper()
	dir := filepath.Join(root, "feedback")
	if err := os.MkdirAll(dir, inboxDirPerm); err != nil {
		t.Fatal(err)
	}
	var b strings.Builder
	for b.Len() <= journalCompactBytes {
		line, err := json.Marshal(Item{
			ID:        adminutil.GenID(),
			CreatedAt: time.Now().UTC().Format(time.RFC3339),
			Type:      "bug",
			Comment:   "старое обращение",
			Logs:      strings.Repeat("x", MaxLogBytes),
		})
		if err != nil {
			t.Fatal(err)
		}
		b.Write(line)
		b.WriteByte('\n')
	}
	if err := os.WriteFile(filepath.Join(dir, "inbox.pending.ndjson"), []byte(b.String()), inboxFilePerm); err != nil {
		t.Fatal(err)
	}
}

// Порог компакции не должен совпадать с потолком одного обращения. Пока они
// были равны, отчёт с максимальным пакетом логов — ровно тем, который разрешено
// прислать, — сам по себе перебрасывал журнал через порог, и каждый такой
// анонимный submit перечитывал и переписывал весь inbox целиком.
func TestMaxSizedReportDoesNotRewriteTheInbox(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	payload, err := json.Marshal(map[string]any{
		"comment": "всё сломалось",
		"logs":    strings.Repeat("L", MaxLogBytes),
	})
	if err != nil {
		t.Fatal(err)
	}
	if w := submit(t, h, string(payload)); w.Code != http.StatusOK {
		t.Fatalf("submit: %d %s", w.Code, w.Body.String())
	}
	h.waitCompaction()
	if _, err := os.Stat(filepath.Join(root, "feedback", "inbox.json")); !os.IsNotExist(err) {
		t.Fatalf("одно обращение с максимальными логами переписало весь inbox: %v", err)
	}
	items, err := h.readAll()
	if err != nil || len(items) != 1 {
		t.Fatalf("readAll = %d items, %v; want 1", len(items), err)
	}
}

// Компакция не должна выполняться на пути запроса: она читает и переписывает
// весь inbox под общим замком, а этот замок берёт и список обращений в админке.
func TestCompactionRunsOffTheRequestPath(t *testing.T) {
	root := t.TempDir()
	h := New(root)
	fillJournal(t, root)

	// Пока замок занят (например, админ листает обращения), запуск компакции
	// обязан вернуться сразу же, ничего не переписав.
	h.mu.Lock()
	started := make(chan struct{})
	go func() {
		h.startCompaction()
		close(started)
	}()
	select {
	case <-started:
	case <-time.After(10 * time.Second):
		h.mu.Unlock()
		t.Fatal("компакция выполняется в вызывающем потоке: запуск не вернулся, пока замок занят")
	}
	if _, err := os.Stat(filepath.Join(root, "feedback", "inbox.json")); !os.IsNotExist(err) {
		t.Errorf("inbox переписан, хотя замок занят: %v", err)
	}
	h.mu.Unlock()

	// А когда замок освободился — журнал слит в массив и удалён.
	h.waitCompaction()
	if _, err := os.Stat(filepath.Join(root, "feedback", "inbox.json")); err != nil {
		t.Fatalf("после компакции нет inbox.json: %v", err)
	}
	if _, err := os.Stat(filepath.Join(root, "feedback", "inbox.pending.ndjson")); !os.IsNotExist(err) {
		t.Fatalf("журнал пережил компакцию: %v", err)
	}
}

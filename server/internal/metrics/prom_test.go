package metrics

import (
	"bytes"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"ChillHub/server/internal/promexp"
)

func newProduct(t *testing.T) (*Product, *promexp.Registry) {
	t.Helper()
	reg := promexp.New()
	return NewProduct(reg), reg
}

func dump(t *testing.T, reg *promexp.Registry) string {
	t.Helper()
	var b strings.Builder
	if err := reg.Write(&b); err != nil {
		t.Fatalf("Write: %v", err)
	}
	return b.String()
}

func mustContain(t *testing.T, out string, lines ...string) {
	t.Helper()
	for _, want := range lines {
		if !strings.Contains(out, want) {
			t.Fatalf("нет строки %q в:\n%s", want, out)
		}
	}
}

func TestRecordInstallAndUpdate(t *testing.T) {
	p, reg := newProduct(t)
	p.Record(Event{Event: "launcher_start", AppVersion: "1.4.0"})
	p.Record(Event{Event: "game_install", GameID: "metro", Result: "ok", DurationMs: 60000, Bytes: 100, FullBytes: 100, FilesDownloaded: 10, FilesTotal: 10})
	p.Record(Event{Event: "game_install", GameID: "metro", Result: "fail"})
	p.Record(Event{Event: "game_launch", GameID: "metro"})

	mustContain(t, dump(t, reg),
		`chillhub_launcher_starts_total{app_version="1.4.0"} 1`,
		`chillhub_game_installs_total{game="metro",result="ok"} 1`,
		`chillhub_game_installs_total{game="metro",result="fail"} 1`,
		`chillhub_game_launches_total{game="metro"} 1`,
		`chillhub_install_duration_seconds_sum{game="metro"} 60`,
		`chillhub_downloaded_bytes_total{game="metro"} 100`,
	)
}

func TestUpdateModeIsDerivedFromFileCounts(t *testing.T) {
	// Это главный продуктовый вопрос лаунчера: обновление должно качать часть
	// сборки, а не сборку целиком. Режим считается по фактическим числам,
	// а не по флагу клиента.
	p, reg := newProduct(t)
	p.Record(Event{Event: "game_update", GameID: "metro", Result: "ok", FilesDownloaded: 3, FilesTotal: 100, Bytes: 5, FullBytes: 500})
	p.Record(Event{Event: "game_update", GameID: "metro", Result: "ok", FilesDownloaded: 100, FilesTotal: 100, Bytes: 500, FullBytes: 500})
	p.Record(Event{Event: "game_update", GameID: "metro", Result: "fail"})

	mustContain(t, dump(t, reg),
		`chillhub_game_updates_total{game="metro",result="ok",mode="diff"} 1`,
		`chillhub_game_updates_total{game="metro",result="ok",mode="full"} 1`,
		`chillhub_game_updates_total{game="metro",result="fail",mode="unknown"} 1`,
		`chillhub_downloaded_bytes_total{game="metro"} 505`,
		`chillhub_build_full_bytes_total{game="metro"} 1000`,
		`chillhub_downloaded_files_total{game="metro"} 103`,
		`chillhub_build_files_total{game="metro"} 200`,
	)
}

func TestIntegrityAndErrors(t *testing.T) {
	p, reg := newProduct(t)
	p.Record(Event{Event: "integrity_check", GameID: "metro", Result: "fail", HashMismatches: 4})
	p.Record(Event{Event: "integrity_check", GameID: "metro", Result: "ok"})
	p.Record(Event{Event: "error", ErrorCode: "sync_io"})
	p.Reject("bad_body")

	mustContain(t, dump(t, reg),
		`chillhub_integrity_checks_total{game="metro",result="fail"} 1`,
		`chillhub_integrity_checks_total{game="metro",result="ok"} 1`,
		`chillhub_hash_mismatches_total{game="metro"} 4`,
		`chillhub_client_errors_total{code="sync_io"} 1`,
		`chillhub_telemetry_rejected_total{reason="bad_body"} 1`,
	)
}

func TestHostileLabelsAreFolded(t *testing.T) {
	// gameId приходит от клиента: без сита одна испорченная сборка навсегда
	// поселила бы мусорный ряд в TSDB.
	p, reg := newProduct(t)
	p.Record(Event{Event: "game_launch", GameID: `metro" or 1=1`})
	p.Record(Event{Event: "game_launch", GameID: ""})
	p.Record(Event{Event: "game_launch", GameID: strings.Repeat("a", 60)})

	out := dump(t, reg)
	mustContain(t, out,
		`chillhub_game_launches_total{game="other"} 2`,
		`chillhub_game_launches_total{game="none"} 1`,
	)
	if strings.Contains(out, "1=1") {
		t.Fatalf("мусорное значение попало в метку:\n%s", out)
	}
}

func TestNilProductIsSafe(_ *testing.T) {
	// Тесты и процессы без экспортёра оставляют Handlers.Prom пустым — приём
	// событий не должен от этого падать.
	var p *Product
	p.Record(Event{Event: "game_launch", GameID: "metro"})
	p.Reject("bad_body")
}

// TestSubmitFeedsCounters проверяет весь путь: HTTP-приём -> файл -> счётчики.
func TestSubmitFeedsCounters(t *testing.T) {
	dir := t.TempDir()
	h := New(dir)
	reg := promexp.New()
	h.Prom = NewProduct(reg)

	body := map[string]any{
		"event": "game_update", "gameId": "metro", "result": "ok",
		"durationMs": 30000, "bytes": 7, "fullBytes": 700,
		"filesDownloaded": 1, "filesTotal": 70,
	}
	b, err := json.Marshal(body)
	if err != nil {
		t.Fatal(err)
	}
	rec := httptest.NewRecorder()
	h.Submit(rec, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/metrics/report", bytes.NewReader(b)))
	if rec.Code != http.StatusOK {
		t.Fatalf("приём вернул %d: %s", rec.Code, rec.Body.String())
	}

	rec = httptest.NewRecorder()
	h.Submit(rec, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/metrics/report", strings.NewReader(`{"event":"нет такого"}`)))
	if rec.Code != http.StatusBadRequest {
		t.Fatalf("неизвестное событие вернуло %d", rec.Code)
	}

	mustContain(t, dump(t, reg),
		`chillhub_game_updates_total{game="metro",result="ok",mode="diff"} 1`,
		`chillhub_downloaded_bytes_total{game="metro"} 7`,
		`chillhub_build_full_bytes_total{game="metro"} 700`,
		`chillhub_telemetry_events_total{event="game_update"} 1`,
		`chillhub_telemetry_rejected_total{reason="unknown_event"} 1`,
	)
}

// TestSubmitClampsFileCounts: клиент присылает числа, которые никто не
// валидировал, и одно значение не должно уводить сумму в бессмыслицу.
func TestSubmitClampsFileCounts(t *testing.T) {
	dir := t.TempDir()
	h := New(dir)
	reg := promexp.New()
	h.Prom = NewProduct(reg)

	b := []byte(`{"event":"game_update","gameId":"metro","result":"ok","filesTotal":999999999999,"filesDownloaded":-5,"hashMismatches":999999999999}`)
	rec := httptest.NewRecorder()
	h.Submit(rec, httptest.NewRequestWithContext(t.Context(), http.MethodPost, "/metrics/report", bytes.NewReader(b)))
	if rec.Code != http.StatusOK {
		t.Fatalf("код %d", rec.Code)
	}
	mustContain(t, dump(t, reg),
		`chillhub_build_files_total{game="metro"} 10000000`,
		`chillhub_hash_mismatches_total{game="metro"} 10000000`,
	)
	if strings.Contains(dump(t, reg), `chillhub_downloaded_files_total{game="metro"} -`) {
		t.Fatal("отрицательное число файлов утекло в счётчик")
	}
}

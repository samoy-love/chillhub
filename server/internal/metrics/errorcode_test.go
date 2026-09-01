package metrics

import (
	"fmt"
	"net/http"
	"strings"
	"testing"

	"ChillHub/server/internal/promexp"
)

// Код ошибки — метка метрики, а /metrics/report открыт всем. Пока поле только
// обрезали по длине, посторонний за несколько минут занимал все слоты семейства
// и настоящие коды до перезапуска сворачивались в "other". Незнакомый код
// теперь становится "unknown", а событие всё равно сохраняется.
func TestSubmitFoldsUnknownErrorCode(t *testing.T) {
	h := New(t.TempDir())
	for i := range promexp.MaxSeries + 10 {
		body := fmt.Sprintf(`{"event":"error","errorCode":"a%08d"}`, i)
		if w := submit(t, h, body); w.Code != http.StatusOK {
			t.Fatalf("submit #%d -> %d %s", i, w.Code, w.Body.String())
		}
	}
	if w := submit(t, h, `{"event":"error","errorCode":"sync_failed"}`); w.Code != http.StatusOK {
		t.Fatalf("submit -> %d", w.Code)
	}

	s := summary(t, h, "")
	if len(s.TopErrors) != 2 {
		t.Fatalf("topErrors = %+v, want two keys: unknown и sync_failed", s.TopErrors)
	}
	for _, b := range s.TopErrors {
		if b.Key != "unknown" && b.Key != "sync_failed" {
			t.Errorf("в сводку попал придуманный код %q", b.Key)
		}
	}
	if s.Totals.Errors != promexp.MaxSeries+11 {
		t.Errorf("errors = %d: событие с чужим кодом должно сохраняться, а не отбрасываться", s.Totals.Errors)
	}
}

// Регистр не должен плодить второй код: лаунчер шлёт нижний, но одна и та же
// ошибка в двух написаниях — это две строки в «Топе ошибок».
func TestSubmitFoldsErrorCodeCase(t *testing.T) {
	h := New(t.TempDir())
	for _, c := range []string{"sync_io", "SYNC_IO", " Sync_IO "} {
		if w := submit(t, h, fmt.Sprintf(`{"event":"error","errorCode":%q}`, c)); w.Code != http.StatusOK {
			t.Fatalf("submit %q -> %d", c, w.Code)
		}
	}
	s := summary(t, h, "")
	if len(s.TopErrors) != 1 || s.TopErrors[0].Key != "sync_io" || s.TopErrors[0].Count != 3 {
		t.Fatalf("topErrors = %+v, want sync_io x3", s.TopErrors)
	}
}

// Список кодов — это ровно то, что шлёт лаунчер. Если он научится новому коду,
// а сервер о нём не узнает, «Топ ошибок» покажет "unknown" вместо причины.
func TestKnownErrorCodesAreTheLauncherOnes(t *testing.T) {
	for _, code := range []string{
		"sync_failed", "sync_io", "manifest_invalid", "no_disk_space",
		"blake3_unavailable", "mods_sync_failed", "mods_manifest_invalid",
		"mods_doorstop_write_failed", "mods_steam_not_found", "mods_exe_missing",
		"mods_launch_failed",
	} {
		if got := normalizeErrorCode(code); got != code {
			t.Errorf("normalizeErrorCode(%q) = %q", code, got)
		}
	}
	if got := normalizeErrorCode(""); got != "" {
		t.Errorf("пустой код = %q, want пустой: его в событии просто нет", got)
	}
	if got := normalizeErrorCode(strings.Repeat("z", maxErrorCode*2)); got != "unknown" {
		t.Errorf("длинный чужой код = %q, want unknown", got)
	}
}

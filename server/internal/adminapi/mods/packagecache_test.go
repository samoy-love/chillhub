package mods

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// ЦЕНА ЛИШНЕГО ЗАПРОСА ЗДЕСЬ — НЕ ТРАФИК, А СЕКУНДЫ НА ГЛАЗАХ У ЧЕЛОВЕКА.
//
// Клиент держит паузу между обращениями к Thunderstore (baseInterval, 320 мс),
// и пауза общая на процесс: она подобрана против живого API, чтобы не ловить
// 429. Значит, каждый лишний запрос — это не «чуть больше сети», а ещё треть
// секунды, пока панель показывает пустой раздел. На боевой панели с пятью
// играми это стоило 1,3 секунды загрузки.
//
// Поэтому проверки ниже считают ЗАПРОСЫ, а не время: время зависит от машины,
// а число обращений — ровно то, что решает задержку.

// countingThunderstore считает обращения к каждому пакету.
func countingThunderstore(t *testing.T, hits *int32) *httptest.Server {
	t.Helper()
	mux := http.NewServeMux()
	mux.HandleFunc("/api/experimental/package/", func(w http.ResponseWriter, r *http.Request) {
		parts := strings.Split(strings.Trim(strings.TrimPrefix(r.URL.Path, "/api/experimental/package/"), "/"), "/")
		if len(parts) != 2 {
			http.NotFound(w, r)
			return
		}
		atomic.AddInt32(hits, 1)
		_ = json.NewEncoder(w).Encode(Package{
			Namespace: parts[0],
			Name:      parts[1],
			FullName:  fmt.Sprintf("%s-%s", parts[0], parts[1]),
			Latest:    PackageVersion{Namespace: parts[0], Name: parts[1], VersionNumber: "2.0.0"},
		})
	})
	return httptest.NewServer(mux)
}

func TestПакетСпрашиваетсяОдинРазНаВсехЧитателей(t *testing.T) {
	var hits int32
	srv := countingThunderstore(t, &hits)
	defer srv.Close()

	cl := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	var c packageCache

	for i := 0; i < 5; i++ {
		if _, err := c.pkg(context.Background(), cl, "Moo", "Moo_Modpack"); err != nil {
			t.Fatalf("запрос %d: %v", i, err)
		}
	}

	if got := atomic.LoadInt32(&hits); got != 1 {
		t.Fatalf("обращений к Thunderstore: %d, ожидалось 1 — остальные обязаны брать готовое", got)
	}
}

func TestОдновременныеЧитателиНеЗаводятСвоихЗапросов(t *testing.T) {
	// Пять `mods/list` приходят одновременно при загрузке панели. Без общего
	// ожидания каждый начал бы свой запрос за тем же пакетом, и очередь
	// клиента снова растянулась бы на 320 мс за штуку.
	var hits int32
	srv := countingThunderstore(t, &hits)
	defer srv.Close()

	cl := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	var c packageCache

	var wg sync.WaitGroup
	errs := make(chan error, 8)
	for i := 0; i < 8; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if _, err := c.pkg(context.Background(), cl, "Moo", "Moo_Modpack"); err != nil {
				errs <- err
			}
		}()
	}
	wg.Wait()
	close(errs)
	for err := range errs {
		t.Fatalf("одновременный запрос: %v", err)
	}

	if got := atomic.LoadInt32(&hits); got != 1 {
		t.Fatalf("обращений к Thunderstore: %d, ожидалось 1", got)
	}
}

func TestРазныеПакетыСпрашиваютсяПорознь(t *testing.T) {
	// Кеш обязан различать пакеты, а не отвечать первым на любой вопрос
	var hits int32
	srv := countingThunderstore(t, &hits)
	defer srv.Close()

	cl := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	var c packageCache

	first, err := c.pkg(context.Background(), cl, "Moo", "Moo_Modpack")
	if err != nil {
		t.Fatal(err)
	}
	second, err := c.pkg(context.Background(), cl, "Other", "Other_Pack")
	if err != nil {
		t.Fatal(err)
	}

	if first.Name == second.Name {
		t.Fatalf("оба ответа про %q — кеш не различает пакеты", first.Name)
	}
	if got := atomic.LoadInt32(&hits); got != 2 {
		t.Fatalf("обращений: %d, ожидалось 2", got)
	}
}

func TestОтказНеЗапоминаетсяНаДесятьМинут(t *testing.T) {
	// Моргнувшая сеть — состояние на секунды. Запомнив отказ, панель показывала
	// бы «состояние неизвестно» до конца TTL, хотя Thunderstore давно отвечает.
	var hits int32
	var fail atomic.Bool
	fail.Store(true)

	mux := http.NewServeMux()
	mux.HandleFunc("/api/experimental/package/", func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&hits, 1)
		if fail.Load() {
			http.Error(w, "нет связи", http.StatusBadGateway)
			return
		}
		_ = json.NewEncoder(w).Encode(Package{Namespace: "Moo", Name: "Moo_Modpack"})
	})
	srv := httptest.NewServer(mux)
	defer srv.Close()

	cl := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	var c packageCache

	if _, err := c.pkg(context.Background(), cl, "Moo", "Moo_Modpack"); err == nil {
		t.Fatal("ожидался отказ")
	}
	before := atomic.LoadInt32(&hits)

	fail.Store(false)
	if _, err := c.pkg(context.Background(), cl, "Moo", "Moo_Modpack"); err != nil {
		t.Fatalf("после восстановления связи: %v", err)
	}
	if atomic.LoadInt32(&hits) <= before {
		t.Fatal("после отказа кеш не пошёл спрашивать заново")
	}
}

func TestПротухшийОтветОтдаётсяСразу(t *testing.T) {
	/* Ждать Thunderstore обязаны только те, кто не знает ничего. Иначе раз в
	   packageTTL кто-то один открывал бы панель за полторы секунды вместо
	   двухсот миллисекунд — и это был бы не всегда один и тот же человек. */
	var hits int32
	release := make(chan struct{})
	mux := http.NewServeMux()
	mux.HandleFunc("/api/experimental/package/", func(w http.ResponseWriter, r *http.Request) {
		if atomic.AddInt32(&hits, 1) > 1 {
			// Второе обращение — фоновое обновление: держим его, чтобы
			// читатель заведомо не мог его дождаться
			<-release
		}
		_ = json.NewEncoder(w).Encode(Package{Namespace: "Moo", Name: "Moo_Modpack"})
	})
	srv := httptest.NewServer(mux)
	defer srv.Close()
	defer close(release)

	cl := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	var c packageCache

	if _, err := c.pkg(context.Background(), cl, "Moo", "Moo_Modpack"); err != nil {
		t.Fatal(err)
	}

	// Состариваем запомненное, не трогая часы процесса
	c.mu.Lock()
	c.rows[PackageKey("Moo", "Moo_Modpack")].at = time.Now().Add(-2 * packageTTL)
	c.mu.Unlock()

	done := make(chan struct{})
	go func() {
		defer close(done)
		if _, err := c.pkg(context.Background(), cl, "Moo", "Moo_Modpack"); err != nil {
			t.Errorf("протухший ответ: %v", err)
		}
	}()

	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("читатель ждёт Thunderstore вместо того, чтобы взять протухшее")
	}
}

func TestНастойчивыйЗапросЗабываетЗапомненное(t *testing.T) {
	// «Обновить» обязано дойти до Thunderstore: иначе оно отвечает тем же
	// снимком, ради обхода которого его и нажали
	var hits int32
	srv := countingThunderstore(t, &hits)
	defer srv.Close()

	cl := NewClient(srv.Client()).WithBases(srv.URL, srv.URL+"/cdn").WithInterval(time.Millisecond)
	var c packageCache

	if _, err := c.pkg(context.Background(), cl, "Moo", "Moo_Modpack"); err != nil {
		t.Fatal(err)
	}
	c.forget()
	if _, err := c.pkg(context.Background(), cl, "Moo", "Moo_Modpack"); err != nil {
		t.Fatal(err)
	}

	if got := atomic.LoadInt32(&hits); got != 2 {
		t.Fatalf("обращений: %d, ожидалось 2 — забытый кеш обязан спросить заново", got)
	}
}

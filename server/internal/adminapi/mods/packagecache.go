package mods

import (
	"context"
	"errors"
	"sync"
	"time"
)

// СПРАШИВАТЬ THUNDERSTORE ОДИН РАЗ НА ПАКЕТ, А НЕ ОДИН РАЗ НА ЗАПРОС.
//
// «Не отстала ли сборка» спрашивают два места: сводка панели (`/summary`) и
// список версий каждой игры (`mods/list`). Оба ходили в Thunderstore сами, и
// оба — на каждую загрузку панели.
//
// Цена этого измерена, а не предположена. Клиент держит паузу 320 мс между
// запросами (см. baseInterval: она подобрана против живого API, чтобы не
// ловить 429), и пауза общая на весь процесс. Пять игр с модпаками — это пять
// запросов подряд, то есть 1,3 секунды, в течение которых панель показывает
// пустой раздел сборок. Проверено по HAR боевой панели: 34, 350, 670, 989 и
// 1311 мс — ровно лестница с шагом в ту самую паузу. С каждой новой игрой она
// становится длиннее.
//
// Кеш это убирает целиком: ответ про пакет живёт packageTTL, и второе место
// берёт готовое вместо своего запроса. Столько же держит сводка (summaryTTL) —
// это одно и то же знание, и разъезжаться этим двум срокам незачем.
//
// ОДИН ЗАПРОС НА ПАКЕТ ДАЖЕ НА ХОЛОДНОМ КЕШЕ. Пять `mods/list` приходят
// одновременно, и без этого каждый начал бы свой запрос за тем же пакетом.
// Ожидающие ждут первого, а не занимают очередь своими запросами.
type packageCache struct {
	mu   sync.Mutex
	rows map[string]*packageEntry
}

// packageEntry — ответ про один пакет: готовый либо ещё едущий.
type packageEntry struct {
	done      chan struct{} // закрывается, когда ответ получен
	at        time.Time
	pkg       *Package
	err       error
	keepError bool

	// renewing — за спиной уже едет обновление этого ответа. Без него
	// каждый читатель протухшего ответа заводил бы своё, и на пяти играх
	// обновление снова стоило бы пяти запросов вместо одного.
	renewing bool
}

// packageTTL — сколько ответ про пакет считается свежим.
//
// Столько же, сколько живёт сводка: обновление модпака выходит не чаще
// нескольких раз в неделю, а панель перезапрашивают на каждое действие.
const packageTTL = summaryTTL

// renewTimeout бережёт фоновое обновление от бесконечного ожидания.
const renewTimeout = 30 * time.Second

// fetchTimeout — сколько первый запрос за пакетом ходит в Thunderstore.
//
// Столько, сколько нужно самому запросу с его повторами, а не сколько готов
// ждать человек у панели: сколько ждать, решает читатель (thunderstoreWait).
const fetchTimeout = 2 * time.Minute

// thunderstoreWait — сколько панель ждёт ответа Thunderstore, прежде чем
// показать раздел без него.
//
// ПАНЕЛЬ НЕ ДОЛЖНА ЛОЖИТЬСЯ ВМЕСТЕ С ЧУЖИМ САЙТОМ. Раньше список сборок и
// сводка ждали Thunderstore по две минуты и полминуты, а панель при запуске
// ждёт все разделы разом. На пустом кеше — сразу после выкатки — недоступный
// Thunderstore оставлял на экране одни заглушки: не открывался ни один раздел,
// хотя от Thunderstore в них зависят две пометки «вышло обновление».
//
// Запрос при этом не обрывается: он доезжает за спиной и кладёт ответ в кеш,
// так что следующее открытие панели пометки уже покажет.
const thunderstoreWait = 5 * time.Second

var errPackageChecking = errors.New("проверяем обновления")

// peek starts one bounded lookup and returns immediately, including on a cold
// cache. HTTP readers must never join the Thunderstore rate-limiter queue.
func (c *packageCache) peek(ctx context.Context, cl *Client, ns, name string) (*Package, error) {
	return c.read(ctx, cl, ns, name, false)
}

// pkg returns the package document, from cache when there is one.
//
// ЖДЁМ THUNDERSTORE ТОЛЬКО ТОГДА, КОГДА НЕ ЗНАЕМ НИЧЕГО. Протухший ответ
// отдаётся сразу, а новый едет за спиной запроса. Иначе раз в packageTTL
// кто-то один открывал бы панель за полторы секунды вместо двухсот
// миллисекунд — и это был бы не всегда один и тот же человек.
//
// Разница между «ответу десять минут» и «ответу десять минут и одна
// секунда» никого не касается: обновления модпаков выходят не чаще
// нескольких раз в неделю.
//
// Блокирующие читатели повторяют отказ сразу. Для HTTP-опроса peek ошибка
// остаётся на минуту: следующий опрос должен увидеть отказ и закончиться,
// а не начать новую проверку, которая снова ответит «проверяем».
//
// ctx ограничивает ожидание, а не сам запрос: читатель, у которого кончилось
// время, уходит без ответа, а запрос доезжает и остаётся в кеше (fetch).
func (c *packageCache) pkg(ctx context.Context, cl *Client, ns, name string) (*Package, error) {
	return c.read(ctx, cl, ns, name, true)
}

func (c *packageCache) read(ctx context.Context, cl *Client, ns, name string, wait bool) (*Package, error) {
	key := PackageKey(ns, name)

	c.mu.Lock()
	if c.rows == nil {
		c.rows = map[string]*packageEntry{}
	}
	e := c.rows[key]
	// Give polling readers time to see a failed check instead of turning
	// every poll into another cold lookup. Successful answers retain their TTL.
	if e != nil && e.err != nil && time.Since(e.at) >= time.Minute {
		delete(c.rows, key)
		e = nil
	}
	if e == nil {
		e = &packageEntry{done: make(chan struct{})}
		c.rows[key] = e
		// Свой контекст, а не запроса: читатель вправе перестать ждать, но
		// оборванный вместе с ним запрос не оставил бы в кеше ничего, и
		// следующий читатель снова ждал бы с нуля.
		//nolint:gosec,contextcheck // контекст запроса здесь был бы ошибкой, а не упущением: он кончается раньше, чем Thunderstore успевает ответить
		go c.fetch(cl, ns, name, key, e)
	}
	if !wait {
		e.keepError = true
	}

	select {
	case <-e.done:
		// HTTP-читатель получает и готовый ответ, и сохранённую ошибку.
		stale := e.err == nil && time.Since(e.at) >= packageTTL
		pkg, err := e.pkg, e.err
		if stale && !e.renewing {
			e.renewing = true
			// Свой контекст, а не запроса: тот отменяется, как только
			// ответ ушёл в браузер, и обновление обрывалось бы на
			// середине каждый раз.
			//nolint:gosec,contextcheck // контекст запроса здесь был бы ошибкой, а не упущением: он отменяется, как только ответ ушёл в браузер
			go c.renew(cl, ns, name, e)
		}
		c.mu.Unlock()
		return pkg, err
	default:
	}
	c.mu.Unlock()
	if !wait {
		return nil, errPackageChecking
	}

	// Запрос уже идёт: ждём его, а не заводим второй.
	select {
	case <-e.done:
		c.mu.Lock()
		defer c.mu.Unlock()
		return e.pkg, e.err
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

// fetch asks Thunderstore about a package nobody knows anything about yet.
func (c *packageCache) fetch(cl *Client, ns, name, key string, e *packageEntry) {
	ctx, cancel := context.WithTimeout(context.Background(), fetchTimeout)
	defer cancel()

	pkg, err := cl.GetPackage(ctx, ns, name)

	c.mu.Lock()
	defer c.mu.Unlock()
	e.pkg, e.err, e.at = pkg, err, time.Now()
	if err != nil && !e.keepError && c.rows[key] == e {
		delete(c.rows, key)
	}
	close(e.done)
}

// renew re-asks Thunderstore behind the request that found the answer stale.
//
// Свой контекст, а не запроса: тот отменяется, как только ответ ушёл в
// браузер, и обновление обрывалось бы на середине каждый раз.
func (c *packageCache) renew(cl *Client, ns, name string, prev *packageEntry) {
	ctx, cancel := context.WithTimeout(context.Background(), renewTimeout)
	defer cancel()

	pkg, err := cl.GetPackage(ctx, ns, name)

	c.mu.Lock()
	defer c.mu.Unlock()
	prev.renewing = false
	if err != nil {
		// Оставляем прежний ответ: он устарел, но это всё ещё знание, а
		// пустота на его месте — нет. Следующий читатель попробует снова.
		return
	}
	prev.pkg = pkg
	prev.at = time.Now()
}

// forget drops everything remembered about packages.
//
// Нужен пересборке: она меняет то, что панель показывает рядом с ответом
// Thunderstore, и держать после неё прежний снимок значит показывать решение,
// принятое по устаревшим данным.
func (c *packageCache) forget() {
	c.mu.Lock()
	c.rows = nil
	c.mu.Unlock()
}

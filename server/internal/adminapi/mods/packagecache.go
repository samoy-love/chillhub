package mods

import (
	"context"
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
	done chan struct{} // закрывается, когда ответ получен
	at   time.Time
	pkg  *Package
	err  error

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
// Ошибку не кешируем: отказ Thunderstore — состояние на секунды, и запомнить
// его на десять минут значит показывать «состояние неизвестно» всё это время
// после одной моргнувшей сети.
func (c *packageCache) pkg(ctx context.Context, cl *Client, ns, name string) (*Package, error) {
	key := PackageKey(ns, name)

	for {
		c.mu.Lock()
		if c.rows == nil {
			c.rows = map[string]*packageEntry{}
		}
		e := c.rows[key]

		if e != nil {
			select {
			case <-e.done:
				if e.err == nil {
					stale := time.Since(e.at) >= packageTTL
					pkg := e.pkg
					if stale && !e.renewing {
						e.renewing = true
						go c.renew(cl, ns, name, e)
					}
					c.mu.Unlock()
					return pkg, nil
				}
				// Ответа нет вовсе — спрашиваем заново.
				delete(c.rows, key)
			default:
				// Запрос уже идёт: ждём его, а не заводим второй.
				c.mu.Unlock()
				select {
				case <-e.done:
					continue
				case <-ctx.Done():
					return nil, ctx.Err()
				}
			}
		}

		e = &packageEntry{done: make(chan struct{})}
		c.rows[key] = e
		c.mu.Unlock()

		e.pkg, e.err = cl.GetPackage(ctx, ns, name)
		e.at = time.Now()
		close(e.done)

		if e.err != nil {
			c.mu.Lock()
			if c.rows[key] == e {
				delete(c.rows, key)
			}
			c.mu.Unlock()
		}
		return e.pkg, e.err
	}
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

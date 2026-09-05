// Данные разделов: загрузка, состояние, устаревание.
//
// ТРИ СОСТОЯНИЯ ВМЕСТО ДВУХ. В панели 1.0 пустое место на экране значило
// сразу три разные вещи: «ничего нет», «ещё грузится» и «запрос упал».
// Отличить их было нельзя, и человек перезагружал страницу наугад.
// Здесь у каждого раздела есть явное состояние, и интерфейс обязан
// показывать разное для каждого.
//
// РАЗДЕЛЫ ГРУЗЯТСЯ ПОРОЗНЬ. Один недоступный эндпоинт не должен оставлять
// пустой всю панель: метрики могут молчать, пока лаунчер прекрасно
// читается. Отсюда и пометка устаревания после записи — по именам
// разделов, а не «перечитать всё».
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.CH2Store = factory();
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  const IDLE = 'idle';
  const LOADING = 'loading';
  const READY = 'ready';
  const FAILED = 'failed';

  /**
   * @param {object} loaders  имя раздела -> async (api) => данные
   * @param {object} deps     { api }
   */
  function createStore(loaders, deps) {
    const d = deps || {};
    const names = Object.keys(loaders);

    const state = {};
    for (const n of names) {
      state[n] = { status: IDLE, data: null, error: null, at: 0 };
    }

    const listeners = new Set();
    const inFlight = new Map();

    const emit = (name) => {
      for (const fn of listeners) fn(name, state[name]);
    };

    const subscribe = (fn) => {
      listeners.add(fn);
      return () => listeners.delete(fn);
    };

    const get = (name) => state[name];

    /** Раздел, который стоит перезапросить: ещё не грузили или упал. */
    const isStale = (name) => {
      const s = state[name];
      return !s || s.status === IDLE || s.status === FAILED;
    };

    /**
     * Загружает раздел. Повторный вызов во время загрузки не порождает
     * второго запроса: щелчок по «Обновить» дважды подряд — обычное дело,
     * и два ответа наперегонки перетирали бы друг друга.
     */
    function load(name, opts) {
      if (!loaders[name]) return Promise.reject(new Error('нет такого раздела: ' + name));
      const force = opts && opts.force;

      if (inFlight.has(name)) return inFlight.get(name);
      if (!force && state[name].status === READY) return Promise.resolve(state[name]);

      state[name] = { status: LOADING, data: state[name].data, error: null, at: state[name].at };
      emit(name);

      const p = Promise.resolve()
        .then(() => loaders[name](d.api))
        .then((data) => {
          state[name] = { status: READY, data: data, error: null, at: Date.now() };
          return state[name];
        })
        .catch((e) => {
          // Прошлые данные не выбрасываем: показать вчерашнее с пометкой
          // честнее, чем пустой экран, — лишь бы пометка была.
          state[name] = { status: FAILED, data: state[name].data, error: e, at: state[name].at };
          return state[name];
        })
        .then((s) => {
          inFlight.delete(name);
          emit(name);
          return s;
        });

      inFlight.set(name, p);
      return p;
    }

    /** Помечает разделы устаревшими и перезапрашивает те, что уже читали. */
    function invalidate(list) {
      const arr = Array.isArray(list) ? list : [list];
      const jobs = [];
      for (const name of arr) {
        if (!loaders[name]) continue;
        const seen = state[name].status !== IDLE;
        state[name] = { status: IDLE, data: state[name].data, error: null, at: state[name].at };
        if (seen) jobs.push(load(name, { force: true }));
        else emit(name);
      }
      return Promise.all(jobs);
    }

    /** Загружает несколько разделов сразу, не роняя один из-за другого. */
    function loadAll(list, opts) {
      const arr = list && list.length ? list : names;
      return Promise.all(arr.map((n) => load(n, opts)));
    }

    /** Сводка «живых» и «упавших» — для честной строки в интерфейсе. */
    function health() {
      const live = names.filter((n) => state[n].status === READY);
      const failed = names.filter((n) => state[n].status === FAILED);
      return { total: names.length, live: live, failed: failed };
    }

    return {
      IDLE, LOADING, READY, FAILED,
      names: names,
      get: get,
      isStale: isStale,
      load: load,
      loadAll: loadAll,
      invalidate: invalidate,
      health: health,
      subscribe: subscribe,
    };
  }

  return { createStore: createStore, IDLE: IDLE, LOADING: LOADING, READY: READY, FAILED: FAILED };
});

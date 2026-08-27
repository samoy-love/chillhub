// Чтение потока NDJSON — вынесено из admin.js по той же причине, что и
// chunk-upload.js/ui-status.js: только обычный require()-имый CommonJS-модуль
// даёт c8 построчное покрытие, а код, вытащенный из admin.js регэкспом и
// исполненный через new Function, V8 с исходным файлом не связывает.
//
// ПОЧЕМУ ВЫНЕСЕНО ИМЕННО СЕЙЧАС: разбор потока был написан в runChunkedUpload
// ДВАЖДЫ — потоковая ветка и фолбэк на res.text() для случая, когда прокси
// буферизует ответ. Сборка модпака — третий длинный процесс с тем же потоком
// событий, и копирование дало бы четвёртую и пятую копию одного цикла. Ветки
// при этом успели разойтись: фолбэк не декодировал байты через TextDecoder и
// на русских сообщениях об ошибке отдавал мусор.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {
  'use strict';

  // splitNdjson делит накопленный буфер на готовые строки и остаток.
  // Возвращает { lines, rest } — остаток дописывается к следующему куску,
  // потому что чанк почти никогда не заканчивается ровно на переводе строки.
  function splitNdjson(buffer) {
    const parts = String(buffer === null || buffer === undefined ? '' : buffer).split(/\r?\n/);
    const rest = parts.pop() || '';
    return { lines: parts.filter(function (l) { return l.length > 0; }), rest };
  }

  // parseEvents превращает строки в объекты, молча пропуская битые.
  //
  // Битая строка — это оборванный поток, а не повод остановить показ прогресса:
  // следующее событие всё равно придёт и перепишет статус. Ошибку самой
  // операции сервер присылает отдельным событием type:"error", и вот его
  // терять нельзя — поэтому оно разбирается наравне с остальными.
  function parseEvents(lines) {
    const out = [];
    for (const line of lines) {
      try {
        out.push(JSON.parse(line));
      } catch (_) {
        // не JSON — пропускаем
      }
    }
    return out;
  }

  // readNdjsonStream читает ответ и вызывает onEvent на каждое событие.
  //
  // Работает и с потоком (res.body.getReader), и без него: если между
  // клиентом и сервером стоит прокси, буферизующий ответ, тела не будет до
  // самого конца, и тогда весь NDJSON приезжает одним куском через res.text().
  // Обе ветки декодируют байты одинаково, и обе отдают одни и те же события.
  //
  // Возвращает число разобранных событий: ноль означает «ответ пришёл, но
  // событий в нём нет», а это отдельная неисправность (обычно — буферизация),
  // которую вызывающий код показывает пользователю.
  async function readNdjsonStream(res, onEvent) {
    const emit = typeof onEvent === 'function' ? onEvent : function () {};
    let count = 0;

    if (res && res.body && typeof res.body.getReader === 'function') {
      const reader = res.body.getReader();
      const dec = new TextDecoder();
      let buf = '';
      for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += dec.decode(value, { stream: true });
        const split = splitNdjson(buf);
        buf = split.rest;
        for (const ev of parseEvents(split.lines)) {
          count++;
          emit(ev);
        }
      }
      // Хвост без завершающего перевода строки — это тоже событие.
      buf += dec.decode();
      for (const ev of parseEvents(splitNdjson(buf + '\n').lines)) {
        count++;
        emit(ev);
      }
      return count;
    }

    const text = await res.text();
    for (const ev of parseEvents(splitNdjson(text + '\n').lines)) {
      count++;
      emit(ev);
    }
    return count;
  }

  return { splitNdjson, parseEvents, readNdjsonStream };
});

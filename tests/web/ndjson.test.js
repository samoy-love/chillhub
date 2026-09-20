// Поток NDJSON: разбор строк и чтение ответа.
//
// ПОЧЕМУ ОТДЕЛЬНЫМ ФАЙЛОМ. `ndjson.js` переиспользуется панелью 2.0 как
// есть — на нём держатся сборка модпака и разбор архива, оба идут
// минутами и оба показывают ход строка за строкой. Проверки же его
// лежали внутри тестов панели 1.0, и вместе с ней ушли бы в никуда:
// модуль остался бы жить без единой проверки.
//
// Сам разбор здесь не переписан — это те же случаи, что проверялись в
// 1.0, слово в слово: произвольная нарезка кусков, хвост без перевода
// строки, буферизованный ответ без потока и битая строка посреди живых.

const test = require('node:test');
const assert = require('node:assert');

const { splitNdjson, parseEvents, readNdjsonStream } = require('../../server/admin_ui/ndjson.js');

test('splitNdjson отдаёт готовые строки и оставляет хвост', () => {
  const first = splitNdjson('{"a":1}\n{"b":2}\n{"c":');
  assert.deepStrictEqual(first.lines, ['{"a":1}', '{"b":2}']);
  // Чанк почти никогда не кончается ровно на переводе строки — незакрытый
  // объект обязан дождаться следующего куска, а не разобраться как мусор.
  assert.strictEqual(first.rest, '{"c":');

  const second = splitNdjson(first.rest + '3}\n');
  assert.deepStrictEqual(second.lines, ['{"c":3}']);
  assert.strictEqual(second.rest, '');
});

test('splitNdjson понимает CRLF и пустые строки', () => {
  const out = splitNdjson('{"a":1}\r\n\r\n{"b":2}\r\n');
  assert.deepStrictEqual(out.lines, ['{"a":1}', '{"b":2}']);
});

test('parseEvents пропускает битую строку, но не теряет соседние', () => {
  const events = parseEvents(['{"type":"start"}', 'не json', '{"type":"done"}']);
  assert.deepStrictEqual(events.map((e) => e.type), ['start', 'done']);
});

// fakeStreamResponse изображает ответ с телом-потоком.
function fakeStreamResponse(chunks) {
  const enc = new TextEncoder();
  let i = 0;
  return {
    body: {
      getReader() {
        return {
          read() {
            return i < chunks.length
              ? Promise.resolve({ done: false, value: enc.encode(chunks[i++]) })
              : Promise.resolve({ done: true, value: undefined });
          },
        };
      },
    },
  };
}

test('readNdjsonStream собирает события из кусков произвольной нарезки', async () => {
  const seen = [];
  // Разрез приходится на середину и ключа, и русского слова: TextDecoder со
  // stream:true обязан склеить многобайтовый символ через границу чанка.
  const n = await readNdjsonStream(
    fakeStreamResponse(['{"type":"start","message":"Ска', 'чив', 'ание"}\n{"type":"done"}\n']),
    (e) => seen.push(e));

  assert.strictEqual(n, 2);
  assert.deepStrictEqual(seen.map((e) => e.type), ['start', 'done']);
  assert.strictEqual(seen[0].message, 'Скачивание');
});

test('readNdjsonStream не теряет последнее событие без перевода строки', async () => {
  const seen = [];
  const n = await readNdjsonStream(fakeStreamResponse(['{"type":"done"}']), (e) => seen.push(e));
  assert.strictEqual(n, 1);
  assert.strictEqual(seen[0].type, 'done');
});

test('readNdjsonStream работает и на буферизованном ответе без потока', async () => {
  // Прокси между админкой и сервером может отдать тело целиком в конце —
  // тогда res.body.getReader отсутствует, а события всё равно должны дойти.
  const seen = [];
  const n = await readNdjsonStream(
    { text: () => Promise.resolve('{"type":"start"}\n{"type":"error","message":"нет места"}\n') },
    (e) => seen.push(e));

  assert.strictEqual(n, 2);
  assert.strictEqual(seen[1].message, 'нет места');
});

test('readNdjsonStream переживает отсутствие обработчика', async () => {
  assert.strictEqual(await readNdjsonStream(fakeStreamResponse(['{"type":"x"}\n']), null), 1);
});

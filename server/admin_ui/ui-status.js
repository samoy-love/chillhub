// Хелперы для заметной строки статуса загрузки — вынесены в отдельный
// CommonJS-модуль по той же причине, что upload-bench.js/rate-estimator.js/
// etc: только require()-имый код даёт c8 построчное покрытие, а не 0% на
// admin.js (см. комментарий в шапке upload-bench.js).
//
// ПОЧЕМУ ЭТО ВООБЩЕ ПОЯВИЛОСЬ: ошибка на любом шаге загрузки (init, чанки,
// complete, распаковка на сервере, сеть) раньше уходила только в notify() —
// маленький <pre id="out"> в самом низу страницы. Заметная строка статуса
// прямо под прогрессом не трогалась и так и оставалась на "Старт
// обработки: ..." навсегда, а ошибка тихо ждала внизу, куда ещё надо было
// долистать. Со стороны это выглядело как "ничего не произошло" при
// полностью успешно залитых чанках — реальный случай на проде.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {

  // setStatusError красит заметную строку статуса под прогрессом в текст
  // ошибки и подсвечивает её. Молча ничего не делает, если элемента нет —
  // вызывающий код передаёт результат getElementById() как есть.
  function setStatusError(el, message) {
    if (!el) return;
    el.textContent = message;
    el.classList.add('text-danger');
  }

  // clearStatusError снимает подсветку ошибки перед новой попыткой загрузки
  // (иначе красный текст остаётся навсегда после первой неудачи) и
  // опционально проставляет новый текст ("Подготовка к загрузке...").
  function clearStatusError(el, resetText) {
    if (!el) return;
    if (resetText !== undefined) el.textContent = resetText;
    el.classList.remove('text-danger');
  }

  return { setStatusError, clearStatusError };
});

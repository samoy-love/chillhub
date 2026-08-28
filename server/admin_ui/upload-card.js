// Разметка карточки «Загрузка ZIP». Раньше она была написана в admin.html
// дважды — на вкладке «Лаунчер» с префиксом up_ и на вкладке «Игры» с
// префиксом man_, — по ~55 строк, включая оба списка из 14 вариантов размера
// чанка. Любая правка требовалась в двух местах, и копии уже начали
// расходиться (у одной подпись «Загружает в content/content/launcher/...»,
// у другой — «.../<gameId>/...», и это единственное, что должно отличаться).
//
// Вынесено отдельным CommonJS-модулем по той же причине, что upload-bench.js
// и ui-status.js: только require()-имый файл c8 считает построчно.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    Object.assign(root, factory());
  }
})(typeof window !== 'undefined' ? window : globalThis, function () {

  // Размеры чанка: от 64 КБ до 512 МБ, по умолчанию 8 МБ.
  const CHUNK_OPTIONS = [
    [65536, '64 КБ'], [131072, '128 КБ'], [262144, '256 КБ'], [524288, '512 КБ'],
    [1048576, '1 МБ'], [2097152, '2 МБ'], [4194304, '4 МБ'], [8388608, '8 МБ'],
    [16777216, '16 МБ'], [33554432, '32 МБ'], [67108864, '64 МБ'],
    [134217728, '128 МБ'], [268435456, '256 МБ'], [536870912, '512 МБ'],
  ];
  const CHUNK_DEFAULT = 8388608;

  // Ключи, которыми различаются две карточки. Идентификаторы полей версии,
  // файла, флага latest и кнопки заданы явно: они исторически не следуют
  // общему префиксу (у игр это `ver`/`man_zip`/`man_upload`, у лаунчера —
  // `up_ver`/`up_zip`/`btnUpload`), и переименовывать их здесь нельзя —
  // на них завязаны и admin.js, и тесты.
  const CARDS = {
    up: {
      title: 'Загрузка новой версии лаунчера (ZIP)',
      verId: 'up_ver',
      fileId: 'up_zip',
      latestId: 'up_latest',
      uploadBtnId: 'btnUpload',
      hint: 'Загружает в <code>content/content/launcher/&lt;version&gt;/files/</code> и создаёт манифест в <code>content/manifests/launcher/</code>.',
    },
    man: {
      title: 'Загрузка версии (ZIP)',
      verId: 'ver',
      fileId: 'man_zip',
      latestId: 'man_latest',
      uploadBtnId: 'man_upload',
      hint: 'Загружает в <code>content/content/&lt;gameId&gt;/&lt;version&gt;/files/</code> и создаёт манифест в <code>content/manifests/&lt;gameId&gt;/</code>.',
    },
  };

  function chunkOptionsHtml() {
    return CHUNK_OPTIONS.map(([value, label]) =>
      '<option value="' + value + '"' + (value === CHUNK_DEFAULT ? ' selected' : '') + '>' + label + '</option>'
    ).join('');
  }

  // uploadCardHtml возвращает разметку карточки для префикса 'up' или 'man'.
  // Неизвестный префикс — пустая строка: вызывающий код просто не найдёт
  // элементов и промолчит, как он делает со всеми getElementById().
  function uploadCardHtml(prefix) {
    const c = CARDS[prefix];
    if (!c) return '';
    const p = prefix + '_';
    return '' +
      '<div class="card">' +
      '  <div class="card-header">' + c.title + '</div>' +
      '  <div class="card-body d-flex flex-column gap-2">' +
      '    <div class="d-flex flex-wrap align-items-end gap-2">' +
      '      <label class="me-2">Версия: <input id="' + c.verId + '" class="form-control form-control-sm d-inline-block" style="width:120px" pattern="\\d+\\.\\d+\\.\\d+" value="1.0.0" placeholder="например 1.0.1" title="Три числа через точку: 1.2.3"></label>' +
      '      <label class="me-2">ZIP: <input id="' + c.fileId + '" type="file" accept=".zip"></label>' +
      '      <label class="me-2"><input type="checkbox" id="' + c.latestId + '" checked> Обновить latest</label>' +
      '      <button type="button" id="' + c.uploadBtnId + '" class="btn btn-sm btn-primary">Загрузить</button>' +
      '      <span id="' + p + 'fit" class="small text-body-secondary"></span>' +
      '    </div>' +
      '    <div id="' + p + 'drop" class="border rounded d-flex align-items-center justify-content-center text-body-secondary" style="height:100px; border-style:dashed !important;">Перетащите ZIP сюда</div>' +
      '    <div class="mt-1" id="' + p + 'prog_wrap" style="max-width:640px; display:none;">' +
      '      <div class="progress" style="height:12px"><div id="' + p + 'pb" class="progress-bar" role="progressbar" style="width:0%"></div></div>' +
      '      <div class="small text-body-secondary mt-1" id="' + p + 'prog_stats" style="display:flex; gap:.75rem; flex-wrap:wrap; align-items:baseline">' +
      '        <span id="' + p + 'prog_pct"></span>' +
      '        <span id="' + p + 'prog_bytes" class="text-body-secondary"></span>' +
      '        <span id="' + p + 'prog_speed"></span>' +
      '        <span id="' + p + 'prog_median" class="text-body-secondary"></span>' +
      '        <span id="' + p + 'prog_peak" class="text-body-secondary"></span>' +
      '        <span id="' + p + 'prog_eta" class="text-body-secondary"></span>' +
      '      </div>' +
      '      <div class="small mt-1" id="' + p + 'prog_text"></div>' +
      '    </div>' +
      '    <div id="' + p + 'speed_wrap" class="mt-1" style="display:none; width:100%; margin-bottom: 8px;">' +
      '      <canvas id="' + p + 'speed" height="60" style="width:100%; background:rgba(255,255,255,0.04); border-radius:4px"></canvas>' +
      '    </div>' +
      // Параметры заливки подбираются автоматически по размеру файла (см.
      // upload-tuning.js), поэтому свёрнуты: разворачивать их нужно только
      // чтобы переопределить подобранное руками или прогнать бенчмарк.
      '    <details class="tuning">' +
      '      <summary>Параметры заливки (чанк, параллельность, очистка)</summary>' +
      '      <div class="d-flex flex-wrap align-items-end gap-3 small">' +
      '        <div>' +
      '          <label class="form-label mb-1 d-block"><input type="checkbox" id="' + p + 'auto_tune" checked> Подбирать автоматически</label>' +
      '          <span id="' + p + 'tune_note" class="text-body-secondary"></span>' +
      '        </div>' +
      '        <div>' +
      '          <label class="form-label mb-1" for="' + p + 'chunk_size">Размер чанка</label>' +
      '          <select id="' + p + 'chunk_size" class="form-select form-select-sm" style="min-width:200px">' + chunkOptionsHtml() + '</select>' +
      '        </div>' +
      '        <div class="flex-grow-1" style="min-width:240px; max-width:520px">' +
      '          <label for="' + p + 'conc" class="form-label mb-1 d-flex justify-content-between"><span>Параллельность</span><span><code id="' + p + 'conc_val">6</code> поток(ов) <span class="ms-2 text-body-secondary" id="' + p + 'active_wrap" style="display:none">активно <code id="' + p + 'active_now">0</code>/<code id="' + p + 'active_cap">0</code></span></span></label>' +
      '          <input id="' + p + 'conc" type="range" class="form-range" min="1" max="100" value="6">' +
      '          <span id="' + p + 'conc_note" class="text-body-secondary"></span>' +
      '        </div>' +
      '        <div class="ms-auto d-flex align-items-end">' +
      '          <button id="' + p + 'cleanup" type="button" class="btn btn-sm btn-outline-warning">Очистить старые/битые</button>' +
      '        </div>' +
      '      </div>' +
      '    </details>' +
      '    <div class="small text-body-secondary">' + c.hint + '</div>' +
      '  </div>' +
      '</div>';
  }

  // mountUploadCards заполняет все плейсхолдеры <div data-upload-card="...">.
  // Вызывается на верхнем уровне admin.js — до того, как код ниже начнёт
  // искать эти элементы по id.
  function mountUploadCards(doc) {
    const d = doc || (typeof document !== 'undefined' ? document : null);
    if (!d) return 0;
    const hosts = d.querySelectorAll('[data-upload-card]');
    let n = 0;
    hosts.forEach((host) => {
      const prefix = host.getAttribute('data-upload-card') || '';
      const html = uploadCardHtml(prefix);
      if (!html) return;
      host.innerHTML = html;
      n++;
    });
    return n;
  }

  return { uploadCardHtml, mountUploadCards, CHUNK_OPTIONS, CHUNK_DEFAULT };
});

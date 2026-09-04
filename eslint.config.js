// ESLint Flat Config for ESLint v9+
// Migrated from .eslintrc.json and .eslintignore

// БАЗОВЫЙ НАБОР ПОДКЛЮЧАЕТСЯ ЯВНО.
//
// В .eslintrc.json стоял "extends": ["eslint:recommended"], и при переезде на
// flat config он потерялся: flat config сам по себе не включает НИ ОДНОГО
// правила. Остались четыре правила из блока ниже, из которых блокировал только
// no-undef, — дубль ключа в объекте, код после return и прочее, что ловит
// базовый набор, проходили гейт зелёными.
//
// Пакет ставится отдельно от eslint (с версии 10 он не входит в его
// зависимости) и обязательно ЛОКАЛЬНО: имя разрешается от каталога этого
// файла, и из глобального префикса npm пакет не виден. Версии — в шапке
// .github/workflows/ci.yml, ставит его тот же шаг, что и eslint.
import js from '@eslint/js';

/** @type {import('eslint').Linter.FlatConfig[]} */
export default [
  // Global ignore patterns (migrated from .eslintignore)
  {
    ignores: [
      'node_modules/',
      'landing/assets/',
      'server/admin_ui/vendor/',
      '**/*.min.js',
      '**/*.min.css',
      'content/'
    ]
  },

  // Базовый набор — отдельным блоком, ДО собственных правил: то, что ниже,
  // должно иметь возможность его переопределить.
  { files: ['**/*.js'], ...js.configs.recommended },

  // Base JS config
  {
    files: ['**/*.js'],
    languageOptions: {
      ecmaVersion: 2021,
      sourceType: 'module',
      // Provide common browser globals to avoid no-undef for frontend bundles
      // (without requiring the `globals` package)
      globals: {
        window: 'readonly',
        document: 'readonly',
        console: 'readonly',
        navigator: 'readonly',
        location: 'readonly',
        history: 'readonly',
        fetch: 'readonly',
        AbortController: 'readonly',
        Request: 'readonly',
        Response: 'readonly',
        Headers: 'readonly',
        URL: 'readonly',
        URLSearchParams: 'readonly',
        setTimeout: 'readonly',
        clearTimeout: 'readonly',
        setInterval: 'readonly',
        clearInterval: 'readonly',
        localStorage: 'readonly',
        sessionStorage: 'readonly',
        Image: 'readonly',
        Event: 'readonly',
        CustomEvent: 'readonly',
        HTMLElement: 'readonly',
        // Additional browser globals used in this project
        requestAnimationFrame: 'readonly',
        cancelAnimationFrame: 'readonly',
        performance: 'readonly',
        IntersectionObserver: 'readonly',
        alert: 'readonly',
        confirm: 'readonly',
        prompt: 'readonly',
        FormData: 'readonly',
        XMLHttpRequest: 'readonly',
        // TextDecoder/TextEncoder — часть веб-платформы и есть в Node: поток
        // NDJSON приходит байтами, и склеивать их иначе нечем.
        TextDecoder: 'readonly',
        TextEncoder: 'readonly',
      }
    },
    rules: {
      // Migrated rules from .eslintrc.json
      'no-unused-vars': ['warn', { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrors: 'all', caughtErrorsIgnorePattern: '^_' }],
      'no-undef': 'error',
      eqeqeq: ['warn', 'always'],
      'no-console': 'off',
      // Пустой catch — намеренная идиома обоих фронтендов: «не вышло — и
      // ладно, показывать нечего». Все остальные пустые блоки базовый набор
      // по-прежнему считает ошибкой.
      'no-empty': ['error', { allowEmptyCatch: true }],
      // В базовый набор no-eval не входит, а во фронтендах лаунчера eval быть
      // не должно вовсе: обе панели собирают html строками, и eval рядом с
      // этим — прямая дорога к исполнению того, что пришло с сервера.
      // Единственное законное место — тест санитайзера, который достаёт
      // функцию из admin.js текстом; там стоит точечное подавление, и без
      // этого правила оно висело неиспользуемым, то есть при --max-warnings 0
      // роняло прогон.
      'no-eval': 'error'
    }
  },

  // Тесты веба запускаются в node (`node --test`), а не в браузере: у них
  // CommonJS-модули и свой набор глобалей. Без этого блока ESLint помечает
  // require/__dirname как no-undef.
  {
    files: ['tests/web/**/*.js'],
    languageOptions: {
      sourceType: 'commonjs',
      globals: {
        require: 'readonly',
        module: 'writable',
        __dirname: 'readonly',
        __filename: 'readonly',
        process: 'readonly',
        Buffer: 'readonly',
      }
    },
    rules: {
      // Тест санитайзера повторяет его регулярку дословно — вместе с
      // диапазоном управляющих символов, ради которого она и написана.
      'no-control-regex': 'off'
    }
  },

  // Targeted overrides for web UI code if needed in the future
  {
    files: ['landing/**/*.js', 'server/admin_ui/**/*.js'],
    // Additional UI-specific settings or rules can go here
  },

  // Admin UI contains many handler stubs and catch params that may be unused by design
  {
    files: ['server/admin_ui/**/*.js'],
    rules: {
      'no-unused-vars': 'off',
      // Два правила базового набора, которые в админке срабатывают на живой
      // код и ни одного дефекта не показывают:
      //
      // no-control-regex — sanitizeUrl вырезает управляющие символы ДО разбора
      // схемы, и класс управляющих в регулярке там не описка, а сама
      // проверка (см. комментарий над ней);
      // no-useless-assignment — инициализация `let x = null` перед цепочкой
      // ветвлений.
      //
      // Выключены до уборки этих мест, а не навсегда: как только останется
      // ноль срабатываний, строки отсюда убрать.
      'no-control-regex': 'off',
      'no-useless-assignment': 'off'
    }
  },

  // upload-bench.js attaches its exports to `window` (see the UMD wrapper at
  // its top) precisely so admin.js can call them as plain globals, the same
  // way it calls every other helper in this directory — ESLint just can't see
  // across the <script> boundary that defines them.
  {
    files: ['server/admin_ui/admin.js'],
    languageOptions: {
      globals: {
        parseBenchList: 'readonly',
        benchCombos: 'readonly',
        pickClosestChunkOption: 'readonly',
        benchUploadOnce: 'readonly',
        benchPlan: 'readonly',
        benchProgress: 'readonly',
        benchProbeBytes: 'readonly',
        makeUiThrottler: 'readonly',
        drawSpeedChart: 'readonly',
        drawMultiLineChart: 'readonly',
        putChunkXHR: 'readonly',
        pendingBytes: 'readonly',
        uploadChunkWithRetries: 'readonly',
        runWorkerPool: 'readonly',
        makeRateEstimator: 'readonly',
        pickUploadParams: 'readonly',
        connectionCap: 'readonly',
        rateWindowMs: 'readonly',
        setStatusError: 'readonly',
        clearStatusError: 'readonly',
        mountUploadCards: 'readonly',
        uploadCardHtml: 'readonly',
        createModsPanel: 'readonly',
        readNdjsonStream: 'readonly'
      }
    }
  },

  // upload-bench.js, ui-throttle.js, speed-chart.js, line-chart.js,
  // chunk-upload.js, rate-estimator.js and ui-status.js are UMD modules:
  // `module` is only referenced behind a `typeof module === 'object'` guard
  // so they work as a plain <script> in the browser too, but that guard
  // doesn't stop no-undef from flagging the bare identifier — the browser
  // globals list above has no `module`/`exports` because real browser code
  // must never see them.
  {
    files: [
      'server/admin_ui/admin-time.js',
      'server/admin_ui/upload-bench.js',
      'server/admin_ui/ui-throttle.js',
      'server/admin_ui/speed-chart.js',
      'server/admin_ui/line-chart.js',
      'server/admin_ui/chunk-upload.js',
      'server/admin_ui/rate-estimator.js',
      'server/admin_ui/upload-tuning.js',
      'server/admin_ui/ui-status.js',
      'server/admin_ui/upload-card.js',
      'server/admin_ui/ndjson.js',
      'server/admin_ui/mods-panel.js',
      'server/admin_ui/feedback-logs.js',
      'server/admin_ui/pending-badges.js',
      'server/admin_ui/registry-diff.js',

      // Панель и лендинг 2.0: те же UMD-модули, тот же guard `typeof module`.
      'server/admin_ui/v2/format.js',
      'server/admin_ui/v2/api.js',
      'server/admin_ui/v2/actions.js',
      'server/admin_ui/v2/store.js',
      'server/admin_ui/v2/sections.js',
      'server/admin_ui/v2/upload.js',
      'server/admin_ui/v2/registry.js',
      'server/admin_ui/v2/news.js',
      'server/admin_ui/v2/gallery.js',
      'server/admin_ui/v2/tuning.js',
      'server/admin_ui/v2/build.js',
      'server/admin_ui/v2/views.js',
      'landing/v2/emu-core.js'
    ],
    languageOptions: {
      globals: {
        // Ветка Node в UMD-обёртке: там же и `require` для соседних модулей
        module: 'readonly',
        require: 'readonly'
      }
    }
  }
];

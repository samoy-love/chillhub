// ESLint Flat Config for ESLint v9+
// Migrated from .eslintrc.json and .eslintignore

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
      }
    },
    rules: {
      // Migrated rules from .eslintrc.json
      'no-unused-vars': ['warn', { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrors: 'all', caughtErrorsIgnorePattern: '^_' }],
      'no-undef': 'error',
      eqeqeq: ['warn', 'always'],
      'no-console': 'off'
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
      'no-unused-vars': 'off'
    }
  }
];

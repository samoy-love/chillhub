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
      }
    },
    rules: {
      // Migrated rules from .eslintrc.json
      'no-unused-vars': ['warn', { argsIgnorePattern: '^_' }],
      'no-undef': 'error',
      eqeqeq: ['warn', 'always'],
      'no-console': 'off'
    }
  },

  // Targeted overrides for web UI code if needed in the future
  {
    files: ['landing/**/*.js', 'server/admin_ui/**/*.js'],
    // Additional UI-specific settings or rules can go here
  }
];

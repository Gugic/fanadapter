import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import eslintConfigPrettier from 'eslint-config-prettier'

export default tseslint.config(
  { ignores: ['dist'] },
  {
    extends: [
      js.configs.recommended,
      ...tseslint.configs.strictTypeChecked,
      ...tseslint.configs.stylisticTypeChecked,
    ],
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2020,
      globals: globals.browser,
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],
      // Two rules that arrived with eslint-plugin-react-hooks v7 (pulled in by
      // the eslint 10 upgrade, which was itself the only fix for the
      // brace-expansion advisory). Both flag PRE-EXISTING App.tsx patterns
      // unrelated to whatever change is in flight, so they're warnings rather
      // than a wall across every future PR — but they are real findings, not
      // noise, and want fixing in their own pass:
      //   set-state-in-effect (8×): the sync-a-prop-into-local-state effect
      //     (`useEffect(() => setY(String(dac.y)), [dac.y])`). Costs a double
      //     render; the fix is deriving the value or keying the component.
      //   purity (1×): `Date.now()` read during render for the capture
      //     countdown label (App.tsx ~2391) — genuinely unstable render output.
      // Promote back to 'error' once App.tsx is cleaned up.
      'react-hooks/set-state-in-effect': 'warn',
      'react-hooks/purity': 'warn',
      // Hardened/Strict Rules overrides:
      '@typescript-eslint/no-unused-vars': [
        'error',
        {
          argsIgnorePattern: '^_',
          varsIgnorePattern: '^_',
        },
      ],
      '@typescript-eslint/restrict-template-expressions': [
        'error',
        {
          allowNumber: true,
          allowBoolean: true,
        },
      ],
      '@typescript-eslint/no-confusing-void-expression': 'off',
      // no-non-null-assertion: left at the default (error). Non-null
      // assertions hide real "undefined" bugs — fix the call site with a
      // guard or fallback instead.
      '@typescript-eslint/no-deprecated': 'off',
      '@typescript-eslint/no-empty-function': 'off',
      '@typescript-eslint/prefer-for-of': 'off',
      '@typescript-eslint/no-misused-promises': [
        'error',
        {
          checksVoidReturn: false,
        },
      ],
      '@typescript-eslint/use-unknown-in-catch-callback-variable': 'off',
      '@typescript-eslint/prefer-nullish-coalescing': 'off',
      // no-unnecessary-condition: left at the default (error). Catches dead
      // conditionals after type narrowing — flip back to 'off' here if a
      // real-world false positive turns out to be too noisy to fix.
    },
  },
  eslintConfigPrettier,
)

import { defineConfig } from '@lingui/cli'
import { formatter } from '@lingui/format-po'

/**
 * The catalogs live at the repo root, not under `frontend/`, because the API reads the same files:
 * `server.po` is compiled into Maki.Api as an embedded resource. One tree means one Weblate project
 * and no way for "chapter" to be translated two different ways on the two sides of the wire.
 *
 * Only `client` is listed here. `server.po` is hand-authored alongside the C# that names its keys.
 * There is no C# extractor; `LocalizationCatalogTests` is what keeps that half honest instead.
 */
export default defineConfig({
  sourceLocale: 'en',
  fallbackLocales: { default: 'en' },
  locales: [
    'en',
    'sv', 'de', 'fr', 'es', 'pt-BR', 'it', 'nl',
    'pl', 'ru', 'tr', 'ja', 'zh-Hans', 'ko',
  ],
  catalogs: [
    {
      path: '<rootDir>/../locales/{locale}/client',
      include: ['<rootDir>/src'],
    },
  ],
  // `lineNumbers: false` keeps the source *file* in the catalog, which is what tells a translator
  // whether they are writing a button label or a failure sentence. The line number would only
  // rewrite half the catalog on every unrelated edit above it.
  format: formatter({ lineNumbers: false }),
})

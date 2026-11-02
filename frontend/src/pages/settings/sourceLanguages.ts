/**
 * Offered as a mixed-language Suwayomi source's chapter language alongside every language a source
 * here already publishes, so the usual ones are there even before any source carries them.
 */
export const COMMON_LANGUAGES = [
  'en', 'ja', 'ko', 'zh', 'zh-Hans', 'zh-Hant', 'es', 'es-419', 'pt', 'pt-BR', 'fr', 'de', 'it', 'ru', 'pl',
  'tr', 'uk', 'id', 'vi', 'th', 'ar',
]
/** What the server accepts as a chapter language; Mihon's `all`/`multi`/`other` fail it. */
export const LANGUAGE_CODE = /^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$/
export const MIXED_LANGUAGE = new Set(['all', 'multi', 'other'])

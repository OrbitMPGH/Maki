import { i18n } from '@lingui/core'

/**
 * The languages Maki ships a translation for. Unlike `ui.titlelanguage`, which accepts any code a
 * metadata provider might tag a title with, this is a closed set: a code with no catalogue behind it
 * would render every string as its internal message id.
 *
 * Labels are endonyms and are never translated. A picker that writes "Tyska" for a Swede and
 * "German" for everyone else is harder to use than one that always writes "Deutsch".
 */
export const SUPPORTED_LOCALES = [
  { code: 'en', label: 'English' },
  { code: 'sv', label: 'Svenska' },
  { code: 'de', label: 'Deutsch' },
  { code: 'fr', label: 'Français' },
  { code: 'es', label: 'Español' },
  { code: 'pt-BR', label: 'Português (Brasil)' },
  { code: 'it', label: 'Italiano' },
  { code: 'nl', label: 'Nederlands' },
  { code: 'pl', label: 'Polski' },
  { code: 'ru', label: 'Русский' },
  { code: 'tr', label: 'Türkçe' },
  { code: 'ja', label: '日本語' },
  { code: 'zh-Hans', label: '简体中文' },
  { code: 'ko', label: '한국어' },
] as const

export type LocaleCode = (typeof SUPPORTED_LOCALES)[number]['code']

export const DEFAULT_LOCALE: LocaleCode = 'en'

/** Read before first paint, so the app never renders a frame in the wrong language. */
const STORAGE_KEY = 'maki-language'

const CODES = SUPPORTED_LOCALES.map((l) => l.code) as readonly string[]

/**
 * The nearest shipped locale to `tag`, or null. Matches exactly first, then on the primary subtag,
 * so a browser asking for `de-AT` or `sv-FI` gets German or Swedish rather than English. `pt-BR` is
 * deliberately the only Portuguese: `pt` resolves to it because a Brazilian UI reads far better to a
 * European Portuguese speaker than an English one does.
 */
export function matchLocale(tag: string | null | undefined): LocaleCode | null {
  if (!tag) return null
  const wanted = tag.trim()
  if (!wanted) return null

  const exact = CODES.find((c) => c.toLowerCase() === wanted.toLowerCase())
  if (exact) return exact as LocaleCode

  const primary = wanted.split('-')[0].toLowerCase()
  const byPrimary = CODES.find((c) => c.split('-')[0].toLowerCase() === primary)
  return (byPrimary as LocaleCode) ?? null
}

/** The stored choice, or null when the user has never picked one. */
export function storedLocale(): LocaleCode | null {
  try {
    return matchLocale(localStorage.getItem(STORAGE_KEY))
  } catch {
    // Private mode, or site data blocked. Fall through to the browser's own preference.
    return null
  }
}

export function storeLocale(locale: LocaleCode): void {
  try {
    localStorage.setItem(STORAGE_KEY, locale)
  } catch {
    // Not being able to remember the choice is not a reason to refuse to make it.
  }
}

/**
 * Forgets the stored choice, so the next resolve falls through to the browser.
 *
 * Choosing "Automatic" clears the server value, and `useLanguageSync` ignores a blank server value
 * on purpose. That leaves this as the only thing still naming a language, and it would win on every
 * load: the setting would read Automatic while the app stayed in whatever was picked last.
 */
export function clearStoredLocale(): void {
  try {
    localStorage.removeItem(STORAGE_KEY)
  } catch {
    // Same as storing: not being able to forget is not a reason to refuse the choice.
  }
}

/** The best shipped match for the browser's own preference, ignoring any stored choice. */
export function browserLocale(): LocaleCode {
  for (const tag of navigator.languages ?? [navigator.language]) {
    const match = matchLocale(tag)
    if (match) return match
  }
  return DEFAULT_LOCALE
}

/**
 * What to activate before the first render: the stored choice, else the best match against the
 * browser's own list, else English. The server's `ui.language` is authoritative once it loads and
 * `LanguageSync` adopts it, but waiting for that round trip would mean a flash of English.
 */
export function resolveInitialLocale(): LocaleCode {
  return storedLocale() ?? browserLocale()
}

/**
 * Loads one catalogue and makes it current. Each locale is its own chunk, so a browser only ever
 * fetches the language it is actually showing.
 *
 * Untranslated entries are filled with English at build time (`fallbackLocales` in
 * `lingui.config.js`), which is what stops a missing translation rendering as its internal message
 * id. That is a silent failure, so the fallback is not optional.
 */
const catalogs = import.meta.glob<{ messages: Record<string, string> }>(
  '../../locales/*/client.po',
)

export async function loadLocale(locale: LocaleCode): Promise<void> {
  const suffix = `/locales/${locale}/client.po`
  const key = Object.keys(catalogs).find((k) => k.endsWith(suffix))
  if (!key) throw new Error(`No catalog for locale "${locale}"`)
  const { messages } = await catalogs[key]()
  i18n.loadAndActivate({ locale, messages })
}

export { i18n }

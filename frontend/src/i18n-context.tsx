import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import { I18nProvider, useLingui } from '@lingui/react'
import type { MessageDescriptor } from '@lingui/core'
import {
  DEFAULT_LOCALE,
  SUPPORTED_LOCALES,
  browserLocale,
  clearStoredLocale,
  i18n,
  loadLocale,
  storeLocale,
  type LocaleCode,
} from './i18n'

interface I18nContextValue {
  locale: LocaleCode
  /** Fetches the catalogue chunk, then activates it. Resolves once the UI has actually changed. */
  setLocale: (locale: LocaleCode) => Promise<void>
  /**
   * Goes back to following the browser: forgets the stored choice and activates whatever the
   * browser asks for. The result is deliberately not stored, because storing it would pin the
   * language again and "Automatic" would last only until the browser's preference next changed.
   */
  followBrowser: () => Promise<void>
  locales: typeof SUPPORTED_LOCALES
}

const I18nChoiceContext = createContext<I18nContextValue | null>(null)

export function useLanguageChoice(): I18nContextValue {
  const ctx = useContext(I18nChoiceContext)
  if (!ctx) throw new Error('useLanguageChoice must be used within AppI18nProvider')
  return ctx
}

/**
 * Sits beside `AppThemeProvider`, and for the same reason: both decide how the first frame looks, so
 * both have to be above everything that renders.
 *
 * Unlike the theme, the choice is not device-local. `localStorage` here is only a cache of the
 * server's `ui.language` so the first paint does not have to wait for a round trip; `LanguageSync`
 * adopts the server value once it arrives. The catalogue for the initial locale is already loaded by
 * the time this mounts (see `main.tsx`), so there is no loading state to render.
 */
export function AppI18nProvider({ children }: { children: React.ReactNode }) {
  const [locale, setLocaleState] = useState<LocaleCode>(() => (i18n.locale as LocaleCode) || DEFAULT_LOCALE)

  const setLocale = useCallback(async (next: LocaleCode) => {
    if (next === i18n.locale) return
    await loadLocale(next)
    storeLocale(next)
    setLocaleState(next)
  }, [])

  const followBrowser = useCallback(async () => {
    // Cleared first and unconditionally. When the browser's language happens to match what is
    // already active there is nothing to load, but the stored choice still has to go, or the next
    // load reads it back and Automatic silently undoes itself.
    clearStoredLocale()
    const next = browserLocale()
    if (next === i18n.locale) return
    await loadLocale(next)
    setLocaleState(next)
  }, [])

  // `lang` drives screen-reader voice selection and the browser's "translate this page?" prompt, so
  // leaving it at the `en` in index.html actively misinforms both. `dir` is always ltr today: none
  // of the shipped languages is RTL, and writing it explicitly means adding one later is a data
  // change here rather than a code change.
  useEffect(() => {
    document.documentElement.lang = locale
    document.documentElement.dir = 'ltr'
  }, [locale])

  const value = useMemo(
    () => ({ locale, setLocale, followBrowser, locales: SUPPORTED_LOCALES }),
    [locale, setLocale, followBrowser],
  )

  return (
    <I18nChoiceContext.Provider value={value}>
      <I18nProvider i18n={i18n}>{children}</I18nProvider>
    </I18nChoiceContext.Provider>
  )
}

/**
 * Adopts the server's `ui.language` once it loads.
 *
 * `localStorage` decides the first paint because waiting for this round trip would mean rendering a
 * frame in the wrong language, but it is only a cache: the server value is what follows somebody to
 * a second device, so it wins whenever the two disagree.
 *
 * A blank server value means "no preference", and is deliberately not treated as a change. Somebody
 * who has never opened the setting keeps whatever their browser asked for rather than being reset to
 * English on every load.
 */
export function useLanguageSync(serverLanguage: string | undefined): void {
  const { locale, setLocale } = useLanguageChoice()

  useEffect(() => {
    if (!serverLanguage) return
    const wanted = serverLanguage as LocaleCode
    if (wanted === locale) return
    void setLocale(wanted)
  }, [serverLanguage, locale, setLocale])
}


/**
 * Renders a label that is either this app's own copy or a piece of data.
 *
 * Several tables hold both: a history entry names a page the app titled, or a series whose title
 * came from a metadata provider. The first has to be translated and the second must not be. Keeping
 * the union and deciding here is what lets those tables stay one list.
 */
export function useLabel(): (label: string | MessageDescriptor) => string {
  const { _ } = useLingui()
  return useCallback((label) => (typeof label === 'string' ? label : _(label)), [_])
}

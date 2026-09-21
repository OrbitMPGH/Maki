import { useQueryClient } from '@tanstack/react-query'
import { useLingui } from '@lingui/react/macro'
import { useSaveUiSettings, useUiSettings, type UiSettings } from '../../api/hooks'
import { useLanguageChoice } from '../../i18n-context'
import type { LocaleCode } from '../../i18n'

/**
 * The picker's options: every shipped language by its endonym, plus the empty value that means
 * "follow the browser". "" is a real choice rather than a null — it deletes the stored row.
 *
 * A hook rather than a table, because only the first entry is translated and a module-scope
 * constant would freeze it in whatever language was active when the module first evaluated.
 */
export function useLanguageOptions(): { value: string; label: string }[] {
  const { t } = useLingui()
  const { locales } = useLanguageChoice()
  return [
    { value: '', label: t`Automatic (match my browser)` },
    ...locales.map((l) => ({ value: l.code, label: l.label })),
  ]
}

/**
 * Applies a language choice: saves it, activates it, and throws away everything the server already
 * rendered in the old one.
 *
 * Shared by the Language card in settings and the one-off announcement modal. Every step below is
 * load-bearing and none of them is obvious, which is reason enough not to have two copies.
 *
 * Returns null while the settings are still loading — the caller's cue to stay read-only rather
 * than save a half-known record, since the UI settings are one record with one PUT.
 */
export function useApplyLanguage(): ((value: string) => void) | null {
  const { data: ui } = useUiSettings()
  const save = useSaveUiSettings()
  const queryClient = useQueryClient()
  const { setLocale, followBrowser } = useLanguageChoice()

  if (!ui) return null

  return (value: string) => {
    save.mutate({ ...ui, language: value })

    // Write the choice into the cache too, because the save above is not awaited and the query
    // keeps its old value until the refetch lands. Without this, `useLanguageSync` wakes up in
    // that window, sees the activated locale disagree with a stale `ui.language`, and puts the old
    // language back along with its stored copy. Picking Automatic is where that hurts: the blank
    // value that eventually arrives is ignored by design, so the undo is permanent.
    queryClient.setQueryData(['settings', 'ui'], (old?: UiSettings) =>
      old ? { ...old, language: value } : old,
    )

    // Activate straight away rather than waiting for the settings query to come back, so the UI
    // changes on the click. `LanguageSync` would eventually do it, but a visible delay on a
    // language picker reads as the setting not having worked.
    const applied = value === '' ? followBrowser() : setLocale(value as LocaleCode)
    void applied.then(() => {
      // Error messages, notification bodies and queue labels are all rendered server-side, so they
      // sit in the query cache in the language they were fetched in. Invalidating one key is not
      // enough; almost every payload carries some.
      void queryClient.clear()
    })
  }
}

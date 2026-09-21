import { useMemo } from 'react'
import { msg } from '@lingui/core/macro'
import { useLingui } from '@lingui/react'
import type { MessageDescriptor } from '@lingui/core'

/** Values of the backend's `SeriesNotificationMode` enum, as they travel over the wire. */
export type SeriesNotificationMode = 'Default' | 'All' | 'Reading' | 'Muted'

/**
 * One label per mode, shared by the series page's switcher and the library's bulk action, so both
 * places a user meets this setting describe it the same way.
 *
 * `Default` defers to the global choice on the notification settings card, which is why its label
 * names neither outcome: what it resolves to is a setting away.
 *
 * Descriptors, not strings, because this list is built once when the module loads. Read it through
 * the hooks below.
 */
const SERIES_NOTIFICATION_MODES: { value: SeriesNotificationMode; label: MessageDescriptor }[] = [
  { value: 'Default', label: msg`Default` },
  { value: 'All', label: msg`Every chapter` },
  { value: 'Reading', label: msg`While reading` },
  { value: 'Muted', label: msg`Muted` },
]

/**
 * The two the *global* default may be. `Default` would point at itself, and a global `Muted` is
 * what switching the per-type "New chapters available" toggle off already does.
 */
const SERIES_DEFAULT_MODES: { value: SeriesNotificationMode; label: MessageDescriptor }[] = [
  { value: 'All', label: msg`Every series` },
  { value: 'Reading', label: msg`Series I'm reading` },
]

/** Longer copy for the bulk modal, where there is room to say what each one actually does. */
const SERIES_NOTIFICATION_HELP: Record<SeriesNotificationMode, MessageDescriptor> = {
  Default: msg`Follows the default on your notification settings.`,
  All: msg`Tells you about every new chapter, whatever your default is.`,
  Reading: msg`Only while you're partway through the series and haven't marked it finished.`,
  Muted: msg`Nothing from this series. Admins still get download failures.`,
}

type Options = { value: SeriesNotificationMode; label: string }[]

/**
 * The lists with their labels rendered, in the shape Mantine's `data` prop wants.
 *
 * Hooks rather than plain functions so the dependency on the active locale is declared once, here,
 * instead of at every call site where forgetting it would leave the old language on screen until
 * something else happened to re-render.
 */
export function useSeriesNotificationOptions(): Options {
  const { _, i18n } = useLingui()
  return useMemo(
    () => SERIES_NOTIFICATION_MODES.map((o) => ({ ...o, label: _(o.label) })),
    [_, i18n.locale],
  )
}

export function useSeriesDefaultOptions(): Options {
  const { _, i18n } = useLingui()
  return useMemo(
    () => SERIES_DEFAULT_MODES.map((o) => ({ ...o, label: _(o.label) })),
    [_, i18n.locale],
  )
}

export function useSeriesNotificationHelp(mode: SeriesNotificationMode): string {
  const { _ } = useLingui()
  return _(SERIES_NOTIFICATION_HELP[mode])
}

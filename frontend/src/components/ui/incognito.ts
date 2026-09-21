import { useMemo } from 'react'
import { msg } from '@lingui/core/macro'
import { useLingui } from '@lingui/react'
import type { MessageDescriptor } from '@lingui/core'

/** Values of the backend's `IncognitoMode` enum, as they travel over the wire. */
export type IncognitoMode = 'Off' | 'ScrobbleOnly' | 'Full'

/**
 * One label per mode, shared by the series page's switcher, the add-series form and the settings
 * rules, so the three places a user meets this setting describe it the same way.
 *
 * Descriptors, not strings: this list is built once when the module loads, so a rendered string
 * here would be stuck in whichever language was active at that moment. Read it through
 * {@link useIncognitoOptions}.
 */
const INCOGNITO_MODES: { value: IncognitoMode; label: MessageDescriptor }[] = [
  { value: 'Off', label: msg`Off` },
  { value: 'ScrobbleOnly', label: msg`No scrobble` },
  { value: 'Full', label: msg`Full` },
]

/**
 * The same list with its labels rendered, in the shape Mantine's `data` prop wants.
 *
 * A hook rather than a function so the dependency on the active locale is declared once, here,
 * instead of at every call site where forgetting it would leave the old language on screen until
 * something else happened to re-render.
 */
export function useIncognitoOptions(): { value: IncognitoMode; label: string }[] {
  const { _, i18n } = useLingui()
  return useMemo(() => INCOGNITO_MODES.map((o) => ({ ...o, label: _(o.label) })), [_, i18n.locale])
}

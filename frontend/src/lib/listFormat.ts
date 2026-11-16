import { i18n } from '@lingui/core'

/** Names joined the way the active language writes a list. */
export function formatList(parts: string[]): string {
  return typeof Intl.ListFormat === 'function' ? new Intl.ListFormat(i18n.locale || undefined).format(parts) : parts.join(', ')
}

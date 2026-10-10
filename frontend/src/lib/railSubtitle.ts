import { i18n } from '@lingui/core'

/**
 * A rail subtitle with its series titles joined in the reader's language. The server renders the
 * sentence with a literal `{list}` or `{titles}` marker and sends the titles separately, because it
 * has no list-format primitive. Rails that name no titles pass through unchanged.
 */
export function railSubtitle(rail: { subtitle?: string | null; subtitleTitles?: string[] | null }): string | null {
  const text = rail.subtitle ?? null
  const titles = rail.subtitleTitles
  if (text === null || !titles || titles.length === 0) return text
  const joined =
    typeof Intl.ListFormat === 'function'
      ? new Intl.ListFormat(i18n.locale || undefined).format(titles)
      : titles.join(', ')
  return text.replace(/\{(?:list|titles)\}/, () => joined)
}

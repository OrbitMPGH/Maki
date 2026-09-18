import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { SelectCards, type SelectCardOption } from './SelectCards'
import { CONTENT_RATING_LABELS, type ContentRating } from '../api/hooks'
import { useLabel } from '../i18n-context'

/** The scale in ascending order, for anything that needs a control per rating. */
export const CONTENT_RATINGS: ContentRating[] = ['safe', 'suggestive', 'erotica', 'pornographic']

/**
 * Descriptors, not strings: rendered at the call site with `useLabel()`, same reasoning as
 * `CONTENT_RATING_LABELS` in `api/hooks.ts`, whose titles this reuses rather than duplicating.
 */
const SUBTITLES: Record<ContentRating, MessageDescriptor> = {
  safe: msg`Only safe titles`,
  suggestive: msg`+ suggestive titles`,
  erotica: msg`+ erotica (default)`,
  pornographic: msg`Everything, no filter`,
}

/**
 * Selecting a card allows it and everything to its left (Safe → Pornographic is an ascending
 * explicitness scale), so the rightmost card is "show all content". Used by both the Settings
 * Discover section and the setup wizard.
 */
export function ContentRatingCards({
  value,
  onChange,
}: {
  value: ContentRating
  onChange: (rating: ContentRating) => void
}) {
  const label = useLabel()
  const options: SelectCardOption<ContentRating>[] = CONTENT_RATINGS.map((rating) => ({
    value: rating,
    title: label(CONTENT_RATING_LABELS[rating]),
    subtitle: label(SUBTITLES[rating]),
  }))
  return <SelectCards options={options} value={value} onChange={onChange} fillLeft={true} />
}

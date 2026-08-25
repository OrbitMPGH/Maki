import { SelectCards, type SelectCardOption } from './SelectCards'
import type { ContentRating } from '../api/hooks'

const OPTIONS: SelectCardOption<ContentRating>[] = [
  { value: 'safe', title: 'Safe', subtitle: 'Only safe titles' },
  { value: 'suggestive', title: 'Suggestive', subtitle: '+ suggestive titles' },
  { value: 'erotica', title: 'Erotica', subtitle: '+ erotica (default)' },
  { value: 'pornographic', title: 'Pornographic', subtitle: 'Everything, no filter' },
]

/** The scale in ascending order, for anything that needs a control per rating. */
export const CONTENT_RATINGS: ContentRating[] = OPTIONS.map((o) => o.value)

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
  return <SelectCards options={OPTIONS} value={value} onChange={onChange} fillLeft={true} />
}

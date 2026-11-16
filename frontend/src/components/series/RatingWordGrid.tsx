import { useLingui } from '@lingui/react/macro'
import { RATING_WORDS, ratingWordFor } from '../../lib/ratingWords'

/** Worst first, so the scale reads left to right like the stars did. */
const ASCENDING = [...RATING_WORDS].reverse()

/**
 * The seven rating words, low to high: a 4 and 3 grid in the rating panel, one row in the quick
 * prompts. Words below the chosen one fill in the rating colour, so the pick also reads as a level.
 * `soFar` dashes the word a "so far" score had; tapping that one calls `onKeep` (when given) so the
 * exact score (7.8, not 8) is kept.
 */
export function RatingWordGrid({
  label,
  value = null,
  soFar = null,
  layout = 'grid',
  disabled = false,
  onPick,
  onKeep,
}: {
  label: string
  value?: number | null
  soFar?: number | null
  layout?: 'grid' | 'row'
  disabled?: boolean
  onPick: (score: number) => void
  onKeep?: () => void
}) {
  const { i18n } = useLingui()
  const chosen = value != null ? ASCENDING.indexOf(ratingWordFor(value)) : -1
  const kept = soFar != null && chosen < 0 ? ASCENDING.indexOf(ratingWordFor(soFar)) : -1
  return (
    <div className="rating-words" data-layout={layout} role="group" aria-label={label}>
      {ASCENDING.map((word, i) => (
        <button
          key={word.score}
          type="button"
          className="rating-word"
          aria-pressed={i === chosen}
          disabled={disabled}
          data-filled={i < chosen || undefined}
          data-on={i === chosen || undefined}
          data-so-far={i === kept || undefined}
          onClick={() => (i === kept && onKeep ? onKeep() : onPick(word.score))}
        >
          {i18n._(word.label)}
        </button>
      ))}
    </div>
  )
}

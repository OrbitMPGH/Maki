import { useLingui } from '@lingui/react/macro'
import { RATING_WORDS, ratingWordFor } from '../../lib/ratingWords'

/** Worst first, so the scale reads left to right like the stars did. */
const ASCENDING = [...RATING_WORDS].reverse()

/**
 * The seven rating words, low to high, as a 4 and 3 grid in the rating panel. Words below the
 * chosen one fill in the rating colour, so the pick also reads as a level.
 */
export function RatingWordGrid({
  label,
  value = null,
  disabled = false,
  onPick,
}: {
  label: string
  value?: number | null
  disabled?: boolean
  onPick: (score: number) => void
}) {
  const { i18n } = useLingui()
  const chosen = value != null ? ASCENDING.indexOf(ratingWordFor(value)) : -1
  return (
    <div className="rating-words" role="group" aria-label={label}>
      {ASCENDING.map((word, i) => (
        <button
          key={word.score}
          type="button"
          className="rating-word"
          aria-pressed={i === chosen}
          disabled={disabled}
          data-filled={i < chosen || undefined}
          data-on={i === chosen || undefined}
          onClick={() => onPick(word.score)}
        >
          {i18n._(word.label)}
        </button>
      ))}
    </div>
  )
}

import { useState, type CSSProperties } from 'react'
import { useRatingText } from '../../api/ratings'

const SCORES = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]

/**
 * Ten score buttons in one row, for the places that ask for a rating and have room for it: the
 * quick prompts, the archive dialog and the reader's last page. Hover previews the score as its word
 * and number under the row. A "so far" score shows grey, filling part of a button for a decimal.
 */
export function RatingScoreButtons({
  label,
  value = null,
  soFar = null,
  disabled = false,
  onPick,
}: {
  label: string
  value?: number | null
  soFar?: number | null
  disabled?: boolean
  onPick: (score: number) => void
}) {
  const ratingText = useRatingText()
  const [preview, setPreview] = useState<number | null>(null)
  const shown = value ?? soFar
  const grey = value == null && soFar != null
  const caption = preview ?? value
  return (
    <div className="rating-scores" data-so-far={grey || undefined}>
      <div className="rating-scores-row" role="group" aria-label={label} onPointerLeave={() => setPreview(null)}>
        {SCORES.map((n) => {
          const fill = preview != null ? (n < preview ? 1 : 0) : shown == null ? 0 : Math.max(0, Math.min(1, shown - (n - 1)))
          const on = preview != null ? n === preview : value != null && Number.isInteger(value) && n === value
          return (
            <button
              key={n}
              type="button"
              className="rating-scores-button tnum"
              style={{ '--fill': `${fill * 100}%` } as CSSProperties}
              data-on={on || undefined}
              data-full={(fill === 1 && !on) || undefined}
              aria-pressed={value != null && n === value}
              aria-label={ratingText(n)}
              disabled={disabled}
              onPointerEnter={() => setPreview(n)}
              onFocus={() => setPreview(n)}
              onBlur={() => setPreview(null)}
              onClick={() => onPick(n)}
            >
              {n}
            </button>
          )
        })}
      </div>
      <span className="rating-scores-caption tnum" aria-hidden>
        {caption != null ? ratingText(caption) : ' '}
      </span>
    </div>
  )
}

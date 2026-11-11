import { Slider, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useScoreShares } from '../api/hooks'
import { formatFixedDecimal } from '../format'

/**
 * The minimum-rating filter's positions, on the 0-10 scale. Scores bunch between 6 and 8 and the
 * long tail of barely rated series sits at 6.0 to 6.4, so even half-point steps from 0 spent most
 * of the slider on almost nothing and jumped by a third of the catalogue near the top. These sit
 * where each step drops roughly a quarter to a third of what the one before kept.
 */
export const RATING_STOPS = [0, 6.5, 6.8, 7, 7.2, 7.4, 7.6, 7.8, 8, 8.3, 8.6, 9]

/**
 * The stop nearest a stored minimum. A preset saved before the stops (7.5, 5) lands on a real
 * stop, never on "any" while it still filters; the value itself is kept until the slider moves.
 */
export function ratingStopIndex(value: number): number {
  if (value <= 0) return 0
  let best = 1
  for (let i = 2; i < RATING_STOPS.length; i++) {
    if (Math.abs(RATING_STOPS[i] - value) < Math.abs(RATING_STOPS[best] - value)) best = i
  }
  return best
}

/**
 * Minimum rating on uneven stops, each labelled with the share of popular series it keeps: the
 * same comparison group and wording as the detail card's score, so a 7.4 reads the same in both.
 * `value` is on the 0-10 scale; callers convert to the dump's 0-100.
 */
export function MinRatingSlider({ value, onChange }: { value: number; onChange: (value: number) => void }) {
  const { t, i18n } = useLingui()
  const { data: shares } = useScoreShares()
  const index = ratingStopIndex(value)

  const shareLabel = (rating: number): string | null => {
    const share = shares?.atOrAbove[Math.round(rating * 10)]
    if (rating <= 0 || share == null) return null
    const pct = i18n.number(Math.max(1, Math.round(share)) / 100, { style: 'percent' })
    return t`Top ${pct} of popular series`
  }

  const stars = (rating: number) => `★ ${formatFixedDecimal(rating, 1)}`
  const ratingLabel = value > 0 ? stars(value) : t`any`
  const share = shareLabel(value)
  const markAt = (rating: number) => RATING_STOPS.indexOf(rating)

  return (
    <div>
      <Text size="sm" fw={500} mb={4}>
        <Trans>Minimum rating: {ratingLabel}</Trans>
      </Text>
      <Slider
        thumbLabel={t`Minimum rating`}
        min={0}
        max={RATING_STOPS.length - 1}
        step={1}
        value={index}
        onChange={(i) => onChange(RATING_STOPS[i])}
        label={(i) => {
          const rating = RATING_STOPS[i]
          if (rating <= 0) return t`any`
          const stopShare = shareLabel(rating)
          return stopShare ? `${stars(rating)} · ${stopShare}` : stars(rating)
        }}
        thumbProps={{ 'aria-valuetext': share ? `${ratingLabel}, ${share}` : ratingLabel }}
        marks={[
          { value: 0, label: t`any` },
          { value: markAt(7), label: '7' },
          { value: markAt(8), label: '8' },
          { value: markAt(9), label: '9' },
        ]}
      />
      {/* The mark labels hang below the slider's box, so the gap is set here rather than as its margin. */}
      <Text size="xs" c="var(--ink-3)" mt="xl" mb="sm" aria-hidden>
        {share ?? ' '}
      </Text>
    </div>
  )
}

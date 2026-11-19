import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { ActionIcon, Button, Group } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useDismissRateHint, useFinalizeRating, useSeriesDetail, useSetRating } from '../../api/hooks'
import { useRatingFormat } from '../../api/ratings'
import { RateHint } from '../../components/series/RateHint'
import { RatingPanelOpener } from '../../components/series/RatingPanelOpener'
import { RATING_STEP, roundRating } from '../../lib/rating'
import { IconAdjustmentsHorizontal, IconMinus, IconPlus } from '../../lib/icons'
import { OVERLAY_Z } from './ReaderSettings'

const NUDGE_DELAY_MS = 600

/**
 * The final rating, asked at the end of the series' last chapter. The same line, endpoints and "keep
 * your so-far score" shortcut as the series page's finished hint; only where it sits is new. Once
 * rated, one decimal nudges it and the breakdown stays a tap away.
 */
export default function ChapterEndRating({ seriesId, soFar }: { seriesId: number; soFar: number | null }) {
  const { t } = useLingui()
  const queryClient = useQueryClient()
  const setRating = useSetRating()
  const finalize = useFinalizeRating()
  const dismiss = useDismissRateHint()
  const { data: series } = useSeriesDetail(seriesId)
  const { precision, format } = useRatingFormat()
  const [answer, setAnswer] = useState<{ rating: number | null } | null>(null)
  const nudgeTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const pendingNudge = useRef<number | null>(null)
  const { mutate: saveRating } = setRating

  // Leaving within the delay saves the last nudge rather than dropping it.
  useEffect(
    () => () => {
      if (nudgeTimer.current) clearTimeout(nudgeTimer.current)
      if (pendingNudge.current != null) saveRating({ seriesId, rating: pendingNudge.current })
    },
    [saveRating, seriesId],
  )

  const settle = (rating: number | null) => {
    setAnswer({ rating })
    void queryClient.invalidateQueries({ queryKey: ['reader-ending', seriesId] })
  }
  const rate = (rating: number) => setRating.mutate({ seriesId, rating }, { onSuccess: () => settle(rating) })

  const panel =
    series && series.ratingBreakdown !== undefined
      ? {
          seriesId,
          value: answer?.rating ?? series.rating,
          finished: true,
          breakdown: series.ratingBreakdown,
          onChange: rate,
        }
      : undefined
  const breakDown = panel && (
    <RatingPanelOpener
      panel={panel}
      zIndex={OVERLAY_Z}
      target={(toggle) => (
        <Button size="compact-sm" variant="subtle" leftSection={<IconAdjustmentsHorizontal size={16} />} onClick={toggle}>
          <Trans>Break it down</Trans>
        </Button>
      )}
    />
  )

  if (answer) {
    if (answer.rating == null) return null
    const current = answer.rating
    const rating = format(current)
    const nudge = (step: number) => {
      const next = roundRating(current + step)
      setAnswer({ rating: next })
      pendingNudge.current = next
      if (nudgeTimer.current) clearTimeout(nudgeTimer.current)
      nudgeTimer.current = setTimeout(() => {
        pendingNudge.current = null
        saveRating({ seriesId, rating: next })
      }, NUDGE_DELAY_MS)
    }
    return (
      <Group gap="xs" justify="center" wrap="wrap">
        <span className="reader-end-note tnum" role="status">
          <Trans>Rated {rating}/10</Trans>
        </span>
        {precision === 'decimal' && (
          <>
            <ActionIcon size="md" radius="xl" variant="default" aria-label={t`Lower by 0.1`} disabled={current <= 1} onClick={() => nudge(-RATING_STEP)}>
              <IconMinus size={16} />
            </ActionIcon>
            <ActionIcon size="md" radius="xl" variant="default" aria-label={t`Raise by 0.1`} disabled={current >= 10} onClick={() => nudge(RATING_STEP)}>
              <IconPlus size={16} />
            </ActionIcon>
          </>
        )}
        {breakDown}
      </Group>
    )
  }

  return (
    <RateHint
      soFar={soFar}
      onRate={rate}
      onKeep={() => finalize.mutate({ seriesId }, { onSuccess: ({ rating }) => settle(rating ?? soFar) })}
      onDismiss={() => dismiss.mutate(seriesId, { onSuccess: () => settle(null) })}
      wrap="wrap"
      panel={panel}
      panelZIndex={OVERLAY_Z}
    />
  )
}

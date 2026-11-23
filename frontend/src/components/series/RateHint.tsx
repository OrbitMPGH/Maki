import { Button } from '@mantine/core'
import { RatingScoreButtons } from './RatingScoreButtons'
import { useRatingFormat } from '../../api/ratings'
import { RatingPanelOpener } from './RatingPanelOpener'
import type { RatingPanel } from './RatingPanel'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconAdjustmentsHorizontal, IconStar } from '../../lib/icons'
import { SeriesHint } from './SeriesHint'

/**
 * A quiet line for a series the reader has finished but not rated: the rating words to rate it in
 * place, and a way to wave it off for good. With a "so far" score it offers that score back, one tap
 * to keep.
 */
export function RateHint({
  soFar: soFarScore = null,
  onRate,
  onKeep,
  onDismiss,
  panel,
  panelZIndex,
}: {
  soFar?: number | null
  onRate: (rating: number) => void
  onKeep?: () => void
  onDismiss: () => void
  /** Offers the rating breakdown beside the stars. */
  panel?: Parameters<typeof RatingPanel>[0]
  panelZIndex?: number
}) {
  const { t } = useLingui()
  const { format } = useRatingFormat()
  const soFar = soFarScore != null ? format(soFarScore) : null
  const keep = soFar != null && onKeep
  return (
    <SeriesHint
      icon={IconStar}
      dismissLabel={t`Dismiss`}
      onDismiss={onDismiss}
      actions={
        (keep || panel) && (
          <>
            {keep && (
              <Button size="compact-sm" variant="light" onClick={onKeep}>
                <Trans>Keep {soFar}</Trans>
              </Button>
            )}
            {panel && (
              <RatingPanelOpener
                panel={panel}
                zIndex={panelZIndex}
                target={(toggle) => (
                  <Button size="compact-sm" variant="subtle" leftSection={<IconAdjustmentsHorizontal size={16} />} onClick={toggle}>
                    <Trans>Break it down</Trans>
                  </Button>
                )}
              />
            )}
          </>
        )
      }
      below={<RatingScoreButtons label={t`Rate this series`} soFar={soFarScore} onPick={onRate} />}
    >
      {soFar != null ? (
        <Trans>You had this at {soFar} so far. Keep it?</Trans>
      ) : (
        <Trans>You've finished this. How was it?</Trans>
      )}
    </SeriesHint>
  )
}

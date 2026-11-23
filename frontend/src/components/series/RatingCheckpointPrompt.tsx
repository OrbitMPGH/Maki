import { Button } from '@mantine/core'
import { RatingScoreButtons } from './RatingScoreButtons'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconStar } from '../../lib/icons'
import type { RatingCheckpoint } from '../../api/hooks'
import { useRatingFormat } from '../../api/ratings'
import { SeriesHint } from './SeriesHint'

/**
 * "How is it so far?", asked at a checkpoint while the reader is partway through. The stars set a
 * "so far" score; with one already set, a single tap keeps it. Ignoring the line is not a dismissal,
 * only the X is.
 */
export function RatingCheckpointPrompt({
  checkpoint,
  soFar: soFarScore = null,
  onRate,
  onDismiss,
}: {
  checkpoint: RatingCheckpoint
  soFar?: number | null
  onRate: (rating: number) => void
  onDismiss: () => void
}) {
  const { t } = useLingui()
  const { format } = useRatingFormat()
  const soFar = soFarScore != null ? format(soFarScore) : null
  return (
    <SeriesHint
      icon={IconStar}
      dismissLabel={t`Dismiss`}
      onDismiss={onDismiss}
      actions={
        soFarScore != null && (
          <Button size="compact-sm" variant="light" onClick={() => onRate(soFarScore)}>
            <Trans>Still a {soFar}</Trans>
          </Button>
        )
      }
      below={
        <RatingScoreButtons label={t`Rate this series so far`} soFar={soFarScore} onPick={onRate} />
      }
    >
      {checkpoint === 'caughtUp' ? (
        <Trans>You're caught up. How is it so far?</Trans>
      ) : checkpoint === 'volumeEnd' ? (
        <Trans>That's the end of a volume. How is it so far?</Trans>
      ) : (
        <Trans>How is it going so far?</Trans>
      )}
    </SeriesHint>
  )
}

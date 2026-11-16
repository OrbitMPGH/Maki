import { ActionIcon, Button, Group, Text } from '@mantine/core'
import { RatingWordGrid } from './RatingWordGrid'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconStar, IconX } from '../../lib/icons'
import type { RatingCheckpoint } from '../../api/hooks'
import { useRatingFormat } from '../../api/ratings'

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
    <div className="series-rate-hint rating-prompt">
      <Group gap="xs" wrap="wrap">
        <IconStar size={16} style={{ flexShrink: 0, color: 'var(--ink-3)' }} aria-hidden />
        <Text size="sm" c="var(--ink-3)">
          {checkpoint === 'caughtUp' ? (
            <Trans>You're caught up. How is it so far?</Trans>
          ) : checkpoint === 'volumeEnd' ? (
            <Trans>That's the end of a volume. How is it so far?</Trans>
          ) : (
            <Trans>How is it going so far?</Trans>
          )}
        </Text>
        {soFarScore != null && (
          <Button size="compact-sm" variant="light" onClick={() => onRate(soFarScore)}>
            <Trans>Still a {soFar}</Trans>
          </Button>
        )}
        <ActionIcon size="sm" variant="subtle" color="var(--neutral)" aria-label={t`Dismiss`} onClick={onDismiss}>
          <IconX size={16} />
        </ActionIcon>
      </Group>
      <RatingWordGrid layout="row"
        label={t`Rate this series so far`}
        soFar={soFarScore}
        onPick={onRate}
        onKeep={soFarScore != null ? () => onRate(soFarScore) : undefined}
      />
    </div>
  )
}

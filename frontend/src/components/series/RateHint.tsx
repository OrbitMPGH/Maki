import { ActionIcon, Button, Group, Text } from '@mantine/core'
import { RatingWordGrid } from './RatingWordGrid'
import { useRatingFormat } from '../../api/ratings'
import { RatingPanelOpener } from './RatingPanelOpener'
import type { RatingPanel } from './RatingPanel'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconAdjustmentsHorizontal, IconStar, IconX } from '../../lib/icons'

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
  wrap = 'nowrap',
  panel,
}: {
  soFar?: number | null
  onRate: (rating: number) => void
  onKeep?: () => void
  onDismiss: () => void
  wrap?: 'wrap' | 'nowrap'
  /** Offers the rating breakdown beside the stars. */
  panel?: Parameters<typeof RatingPanel>[0]
}) {
  const { t } = useLingui()
  const { format } = useRatingFormat()
  const soFar = soFarScore != null ? format(soFarScore) : null
  return (
    <div className="series-rate-hint rating-prompt">
      <Group gap="xs" wrap={wrap}>
        <IconStar size={16} style={{ flexShrink: 0, color: 'var(--ink-3)' }} aria-hidden />
        <Text size="sm" c="var(--ink-3)">
          {soFar != null ? (
            <Trans>You had this at {soFar} so far. Keep it?</Trans>
          ) : (
            <Trans>You've finished this. How was it?</Trans>
          )}
        </Text>
        {soFar != null && onKeep && (
          <Button size="compact-sm" variant="light" onClick={onKeep}>
            <Trans>Keep {soFar}</Trans>
          </Button>
        )}
        {panel && (
          <RatingPanelOpener
            panel={panel}
            target={(toggle) => (
              <Button size="compact-sm" variant="subtle" leftSection={<IconAdjustmentsHorizontal size={16} />} onClick={toggle}>
                <Trans>Break it down</Trans>
              </Button>
            )}
          />
        )}
        <ActionIcon size="sm" variant="subtle" color="var(--neutral)" aria-label={t`Dismiss`} onClick={onDismiss}>
          <IconX size={16} />
        </ActionIcon>
      </Group>
      <RatingWordGrid layout="row" label={t`Rate this series`} soFar={soFarScore} onPick={onRate} onKeep={onKeep} />
    </div>
  )
}

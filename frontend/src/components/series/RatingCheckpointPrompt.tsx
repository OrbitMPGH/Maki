import { ActionIcon, Button, Group, Rating, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconStar, IconX } from '@tabler/icons-react'
import type { RatingCheckpoint } from '../../api/hooks'

/**
 * "How is it so far?", asked at a checkpoint while the reader is partway through. The stars set a
 * "so far" score; with one already set, a single tap keeps it. Ignoring the line is not a dismissal,
 * only the X is.
 */
export function RatingCheckpointPrompt({
  checkpoint,
  soFar = null,
  onRate,
  onDismiss,
}: {
  checkpoint: RatingCheckpoint
  soFar?: number | null
  onRate: (rating: number) => void
  onDismiss: () => void
}) {
  const { t } = useLingui()
  return (
    <Group className="series-rate-hint" gap="xs" mt="md" wrap="wrap">
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
      <Rating
        size="sm"
        count={5}
        fractions={2}
        defaultValue={soFar != null ? soFar / 2 : 0}
        role="group"
        aria-label={t`Rate this series so far`}
        onChange={(v) => {
          const rating = Math.round(v * 2)
          if (rating > 0) onRate(rating)
        }}
      />
      {soFar != null && (
        <Button size="compact-sm" variant="light" onClick={() => onRate(soFar)}>
          <Trans>Still a {soFar}</Trans>
        </Button>
      )}
      <ActionIcon size="sm" variant="subtle" color="gray" aria-label={t`Dismiss`} onClick={onDismiss}>
        <IconX size={14} />
      </ActionIcon>
    </Group>
  )
}

import { ActionIcon, Button, Group, Rating, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconStar, IconX } from '@tabler/icons-react'

/**
 * A quiet line for a series the reader has finished but not rated: stars to rate it in place, and a
 * way to wave it off for good. With a "so far" score it offers that score back, one tap to keep.
 */
export function RateHint({
  soFar = null,
  onRate,
  onKeep,
  onDismiss,
}: {
  soFar?: number | null
  onRate: (rating: number) => void
  onKeep?: () => void
  onDismiss: () => void
}) {
  const { t } = useLingui()
  return (
    <Group className="series-rate-hint" gap="xs" mt="md" wrap="nowrap">
      <IconStar size={16} style={{ flexShrink: 0, color: 'var(--ink-3)' }} aria-hidden />
      <Text size="sm" c="var(--ink-3)">
        {soFar != null ? (
          <Trans>You had this at {soFar} so far. Keep it?</Trans>
        ) : (
          <Trans>You've finished this. How was it?</Trans>
        )}
      </Text>
      <Rating
        size="sm"
        count={5}
        fractions={2}
        defaultValue={soFar != null ? soFar / 2 : 0}
        role="group"
        aria-label={t`Rate this series`}
        onChange={(v) => {
          const rating = Math.round(v * 2)
          if (rating > 0) onRate(rating)
        }}
      />
      {soFar != null && onKeep && (
        <Button size="compact-sm" variant="light" onClick={onKeep}>
          <Trans>Keep {soFar}</Trans>
        </Button>
      )}
      <ActionIcon size="sm" variant="subtle" color="gray" aria-label={t`Dismiss`} onClick={onDismiss}>
        <IconX size={14} />
      </ActionIcon>
    </Group>
  )
}

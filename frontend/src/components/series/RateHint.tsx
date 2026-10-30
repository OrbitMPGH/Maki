import { ActionIcon, Group, Rating, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconStar, IconX } from '@tabler/icons-react'

/** A quiet line for a series the reader has finished but not rated: stars to rate it in place, and a way to wave it off for good. */
export function RateHint({ onRate, onDismiss }: { onRate: (rating: number) => void; onDismiss: () => void }) {
  const { t } = useLingui()
  return (
    <Group className="series-rate-hint" gap="xs" mt="md" wrap="nowrap">
      <IconStar size={16} style={{ flexShrink: 0, color: 'var(--ink-3)' }} aria-hidden />
      <Text size="sm" c="var(--ink-3)">
        <Trans>You've finished this. How was it?</Trans>
      </Text>
      <Rating
        size="sm"
        count={5}
        fractions={2}
        role="group"
        aria-label={t`Rate this series`}
        onChange={(v) => {
          const rating = Math.round(v * 2)
          if (rating > 0) onRate(rating)
        }}
      />
      <ActionIcon size="sm" variant="subtle" color="gray" aria-label={t`Dismiss`} onClick={onDismiss}>
        <IconX size={14} />
      </ActionIcon>
    </Group>
  )
}

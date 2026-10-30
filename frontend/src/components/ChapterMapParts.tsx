import { Button, Group, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'

/** "0" → "Ch. 0"; a run of integers → "Ch. 0 to 8"; anything else lists them. */
export function useRangeText(labels: string[]): string {
  const { t } = useLingui()
  if (labels.length === 0) return ''
  const first = labels[0]
  const last = labels[labels.length - 1]
  if (labels.length === 1) return t`Ch. ${first}`
  const nums = labels.map(Number)
  const run =
    nums.every((n, i) => Number.isInteger(n) && (i === 0 || n === nums[i - 1] + 1))
  const list = labels.join(', ')
  return run ? t`Ch. ${first} to ${last}` : t`Ch. ${list}`
}

export function AllNone({ onAll, onNone }: { onAll: () => void; onNone: () => void }) {
  return (
    <Group gap={4}>
      <Button size="compact-xs" variant="subtle" onClick={onAll}>
        <Trans>All</Trans>
      </Button>
      <Button size="compact-xs" variant="subtle" onClick={onNone}>
        <Trans>None</Trans>
      </Button>
    </Group>
  )
}

export function MapLegend({ className, label }: { className: string; label: string }) {
  return (
    <Group gap={6} wrap="nowrap">
      <span className={`relink-swatch ${className}`} aria-hidden />
      <Text size="xs" c="var(--ink-3)">
        {label}
      </Text>
    </Group>
  )
}

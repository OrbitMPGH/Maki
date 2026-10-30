import { ActionIcon, Button, Group, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconArchive, IconX } from '@tabler/icons-react'

/** A quiet line for a completed series everyone has finished: offers the archive and can be waved off for good. */
export function ArchiveHint({ onArchive, onDismiss }: { onArchive: () => void; onDismiss: () => void }) {
  const { t } = useLingui()
  return (
    <Group className="series-archive-hint" gap="xs" mt="md" wrap="nowrap">
      <IconArchive size={16} style={{ flexShrink: 0, color: 'var(--ink-3)' }} aria-hidden />
      <Text size="sm" c="var(--ink-3)">
        <Trans>You've finished this. Archive it?</Trans>
      </Text>
      <Button size="compact-xs" variant="subtle" onClick={onArchive}>
        <Trans context="series">Archive</Trans>
      </Button>
      <ActionIcon size="sm" variant="subtle" color="gray" aria-label={t`Dismiss`} onClick={onDismiss}>
        <IconX size={14} />
      </ActionIcon>
    </Group>
  )
}

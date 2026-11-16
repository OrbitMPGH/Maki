import { ActionIcon, Button, Group, Text } from '@mantine/core'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { IconCircleCheck, IconX } from '../../lib/icons'

/**
 * A quiet line for a finished series whose every kept chapter is read, while some were switched off or
 * a chapter was still expected when the last one was read. Switched-off chapters may be skipped extras
 * or the rest of a series being tried out, so Maki asks instead of guessing.
 */
export function CompleteHint({
  leftOut,
  onComplete,
  onDismiss,
}: {
  leftOut: number
  onComplete: () => void
  onDismiss: () => void
}) {
  const { t } = useLingui()
  return (
    <Group className="series-complete-hint" gap="xs" mt="md" wrap="nowrap">
      <IconCircleCheck size={16} style={{ flexShrink: 0, color: 'var(--ink-3)' }} aria-hidden />
      <Text size="sm" c="var(--ink-3)">
        {leftOut > 0 ? (
          <Plural
            value={leftOut}
            one="You've read every chapter you kept. # chapter was left out. Done with this series?"
            other="You've read every chapter you kept. # chapters were left out. Done with this series?"
          />
        ) : (
          <Trans>You've read every chapter. Done with this series?</Trans>
        )}
      </Text>
      <Button size="compact-xs" variant="subtle" onClick={onComplete}>
        <Trans>Mark completed</Trans>
      </Button>
      <ActionIcon size="sm" variant="subtle" color="gray" aria-label={t`Not yet`} onClick={onDismiss}>
        <IconX size={16} />
      </ActionIcon>
    </Group>
  )
}

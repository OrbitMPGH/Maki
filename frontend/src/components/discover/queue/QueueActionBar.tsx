import { ActionIcon, Group, Tooltip } from '@mantine/core'
import { IconArrowBackUp, IconHeart, IconInfoCircle, IconX, IconMoodSad } from '@tabler/icons-react'
import { useLingui } from '@lingui/react/macro'

/**
 * The primary control surface: drag is the enhancement, these buttons are what always works.
 * See design 4.4.
 */
export function QueueActionBar({
  disabled,
  canUndo,
  onWant,
  onSkip,
  onNotForMe,
  onInfo,
  onUndo,
}: {
  disabled: boolean
  canUndo: boolean
  onWant: () => void
  onSkip: () => void
  onNotForMe: () => void
  onInfo: () => void
  onUndo: () => void
}) {
  const { t } = useLingui()
  return (
    <Group className="queue-action-bar" justify="center" gap="md" wrap="nowrap">
      <Tooltip label={t`Undo`} withArrow>
        <ActionIcon
          className="queue-action queue-action-undo"
          size="lg"
          variant="default"
          radius="xl"
          disabled={!canUndo}
          aria-label={t`Undo`}
          onClick={onUndo}
        >
          <IconArrowBackUp size={18} />
        </ActionIcon>
      </Tooltip>
      <Tooltip label={t`Skip`} withArrow>
        <ActionIcon
          className="queue-action queue-action-skip"
          size="xl"
          variant="default"
          radius="xl"
          disabled={disabled}
          aria-label={t`Skip`}
          onClick={onSkip}
        >
          <IconX size={22} />
        </ActionIcon>
      </Tooltip>
      <Tooltip label={t`Not for me`} withArrow>
        <ActionIcon
          className="queue-action queue-action-not-for-me"
          size="lg"
          variant="default"
          radius="xl"
          disabled={disabled}
          aria-label={t`Not for me`}
          onClick={onNotForMe}
        >
          <IconMoodSad size={18} />
        </ActionIcon>
      </Tooltip>
      <Tooltip label={t`Add to shortlist`} withArrow>
        <ActionIcon
          className="queue-action queue-action-want"
          size="xl"
          variant="filled"
          color="var(--brand)"
          radius="xl"
          disabled={disabled}
          aria-label={t`Add to shortlist`}
          onClick={onWant}
        >
          <IconHeart size={22} />
        </ActionIcon>
      </Tooltip>
      <Tooltip label={t`Details`} withArrow>
        <ActionIcon
          className="queue-action queue-action-info"
          size="lg"
          variant="default"
          radius="xl"
          disabled={disabled}
          aria-label={t`Details`}
          onClick={onInfo}
        >
          <IconInfoCircle size={18} />
        </ActionIcon>
      </Tooltip>
    </Group>
  )
}

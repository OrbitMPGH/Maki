import { errorText } from '../../api/errorText'
import { useNavigate } from 'react-router-dom'
import { ActionIcon, Button, Group, Menu, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { IconBook, IconDots, IconEyeOff } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useHideHomeReading, type HomeReadingItem } from '../../api/hooks'

/**
 * The small menu on a reading-rail card or tile. A sibling of the card's link, never inside it:
 * React bubbles clicks out of the portalled dropdown along the component tree, so a menu inside
 * the link would open the reader on every item click.
 */
export function ReadingCardMenu({
  item,
  className,
  variant = 'filled',
  size = 28,
}: {
  item: HomeReadingItem
  className: string
  /** `default` sits it in a row of buttons; the filled round default floats over a cover. */
  variant?: 'filled' | 'default'
  size?: number
}) {
  const { t } = useLingui()
  const navigate = useNavigate()
  const hide = useHideHomeReading()
  const { seriesId, seriesTitle } = item

  // mutateAsync, not mutate with callbacks: the optimistic update unmounts this card at once, and
  // per-call callbacks never fire for a component that is gone.
  const remove = async () => {
    try {
      await hide.mutateAsync({ seriesId, hidden: true })
    } catch (error) {
      const reason = errorText(error)
      notifications.show({ color: 'var(--danger)', message: t`Could not remove ${seriesTitle}: ${reason}` })
      return
    }
    const id = notifications.show({
      autoClose: 8000,
      message: (
        <Group gap="xs" wrap="nowrap" justify="space-between">
          <Text size="sm">
            <Trans>Removed {seriesTitle}. It comes back when you read it again.</Trans>
          </Text>
          <Button
            size="xs"
            variant="subtle"
            style={{ flexShrink: 0 }}
            onClick={() => {
              notifications.hide(id)
              hide.mutateAsync({ seriesId, hidden: false }).catch((error: unknown) => {
                const reason = errorText(error)
                notifications.show({ color: 'var(--danger)', message: t`Could not restore ${seriesTitle}: ${reason}` })
              })
            }}
          >
            <Trans>Undo</Trans>
          </Button>
        </Group>
      ),
    })
  }

  return (
    <Menu withinPortal position="bottom-end" shadow="md">
      <Menu.Target>
        <ActionIcon
          className={className}
          variant={variant}
          radius={variant === 'filled' ? 'xl' : 'md'}
          size={size}
          aria-label={t`More options for ${seriesTitle}`}
        >
          <IconDots size={16} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconBook size={16} />} onClick={() => navigate(`/series/${seriesId}`)}>
          <Trans>Open series</Trans>
        </Menu.Item>
        <Menu.Divider />
        <Menu.Item leftSection={<IconEyeOff size={16} />} onClick={() => void remove()}>
          <Trans>Remove from Continue reading</Trans>
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  )
}

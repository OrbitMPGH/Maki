import {
  ActionIcon,
  Anchor,
  Box,
  Group,
  Indicator,
  Popover,
  ScrollArea,
  Stack,
  Text,
  Tooltip,
  UnstyledButton,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import { IconAlertTriangle, IconBell, IconBellOff, IconCircleCheck } from '@tabler/icons-react'
import { useNavigate } from 'react-router-dom'
import {
  useInbox,
  useInboxUnread,
  useMarkAllInboxRead,
  useMarkInboxRead,
  type InboxItem,
} from '../api/inbox'
import { relativeTime } from './ui/time'

/**
 * Header bell over the in-app notification inbox.
 * <p>
 * The badge count is its own query so it is live before anything opens the dropdown; the feed is
 * only fetched once the popover is opened, which keeps a page load to one small count request.
 */
export function NotificationBell() {
  const [opened, { toggle, close }] = useDisclosure(false)
  const navigate = useNavigate()

  const { data: unread } = useInboxUnread()
  const { data, isLoading } = useInbox()
  const markRead = useMarkInboxRead()
  const markAll = useMarkAllInboxRead()

  // The bell shows a slice; the page shows the history. Only the first page is ever rendered here.
  const items = data?.pages[0]?.items ?? []
  const count = unread?.count ?? 0

  function open(item: InboxItem) {
    if (!item.read) markRead.mutate(item.id)
    close()
    if (item.url) navigate(item.url)
  }

  return (
    <Popover
      width={380}
      position="bottom-end"
      withArrow
      shadow="md"
      opened={opened}
      onChange={toggle}
    >
      <Popover.Target>
        <Tooltip label={count > 0 ? `${count} unread` : 'Notifications'} withArrow disabled={opened}>
          <Indicator
            size={16}
            color="brand"
            label={count > 99 ? '99+' : count}
            disabled={count === 0}
            withBorder
          >
            <ActionIcon
              variant="subtle"
              color="gray"
              aria-label="Notifications"
              onClick={toggle}
            >
              <IconBell size={19} />
            </ActionIcon>
          </Indicator>
        </Tooltip>
      </Popover.Target>

      <Popover.Dropdown p={0}>
        <Group justify="space-between" px="sm" py={8} wrap="nowrap">
          <Text fw={650} size="sm">
            Notifications
          </Text>
          {count > 0 && (
            <Anchor component="button" type="button" size="xs" onClick={() => markAll.mutate()}>
              Mark all read
            </Anchor>
          )}
        </Group>

        {isLoading ? (
          <Text size="xs" c="dimmed" px="sm" pb="sm">
            Loading…
          </Text>
        ) : items.length === 0 ? (
          <Stack align="center" gap={6} px="sm" py="lg">
            <IconBellOff size={22} opacity={0.4} />
            <Text size="xs" c="dimmed">
              Nothing yet
            </Text>
          </Stack>
        ) : (
          <ScrollArea.Autosize mah={380} type="auto">
            <Stack gap={0}>
              {items.map((item) => (
                <NotificationRow key={item.id} item={item} onOpen={open} />
              ))}
            </Stack>
          </ScrollArea.Autosize>
        )}

        <Box px="sm" py={8} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
          <Anchor
            component="button"
            type="button"
            size="xs"
            onClick={() => {
              close()
              navigate('/notifications')
            }}
          >
            See all notifications
          </Anchor>
        </Box>
      </Popover.Dropdown>
    </Popover>
  )
}

function NotificationRow({
  item,
  onOpen,
}: {
  item: InboxItem
  onOpen: (item: InboxItem) => void
}) {
  return (
    <UnstyledButton
      onClick={() => onOpen(item)}
      px="sm"
      py={8}
      style={{
        // Unread rows carry a leading accent rather than a different background: the dropdown is
        // short enough that a wash of colour on most rows reads as an error state.
        borderLeft: `2px solid ${item.read ? 'transparent' : 'var(--mantine-color-brand-6)'}`,
      }}
      className="inbox-row"
    >
      <Group gap="xs" wrap="nowrap" align="flex-start">
        <Box mt={2}>
          <NotificationVisual item={item} size={26} />
        </Box>
        <Stack gap={2} style={{ minWidth: 0 }}>
          <Text size="xs" fw={item.read ? 500 : 650} lineClamp={1}>
            {item.title}
          </Text>
          <Text size="xs" c="dimmed" lineClamp={2}>
            {item.body}
          </Text>
          {/* fz, not size: `size` takes a token ("xs"), and a raw number there resolves against
              --mantine-line-height-{n}, which does not exist and lands as line-height: 100px. */}
          <Text fz={10} lh={1.5} c="dimmed">
            {relativeTime(item.createdAt)}
          </Text>
        </Stack>
      </Group>
    </UnstyledButton>
  )
}

export function LevelIcon({ level }: { level: InboxItem['level'] }) {
  if (level === 'error') return <IconAlertTriangle size={15} color="var(--mantine-color-red-6)" />
  if (level === 'warning') return <IconAlertTriangle size={15} color="var(--mantine-color-yellow-6)" />
  return <IconCircleCheck size={15} color="var(--mantine-color-brand-6)" />
}

/**
 * The leading visual for a notification row: the series' poster when there is one, otherwise the
 * severity icon.
 * <p>
 * A warning or error keeps its icon as a corner badge over the poster. Dropping it whenever a cover
 * exists would make "download failed" and "chapters downloaded" look identical at a glance, which is
 * the one distinction the row's colour is carrying.
 */
export function NotificationVisual({ item, size }: { item: InboxItem; size: number }) {
  if (!item.coverUrl) {
    return <LevelIcon level={item.level} />
  }

  return (
    <Box className="inbox-cover" style={{ width: size, height: Math.round(size * 1.45) }}>
      <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />
      {item.level !== 'info' && (
        <span className="inbox-cover-badge">
          <IconAlertTriangle
            size={11}
            color={item.level === 'error' ? 'var(--mantine-color-red-6)' : 'var(--mantine-color-yellow-6)'}
          />
        </span>
      )}
    </Box>
  )
}

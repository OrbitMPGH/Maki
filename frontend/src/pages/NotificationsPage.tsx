import {
  ActionIcon,
  Box,
  Button,
  Card,
  Chip,
  Group,
  Loader,
  Stack,
  Switch,
  Text,
  Tooltip,
  UnstyledButton,
} from '@mantine/core'
import { IconBellOff, IconSettings, IconX } from '@tabler/icons-react'
import { useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import {
  INBOX_CATEGORIES,
  useClearInbox,
  useDismissInbox,
  useInbox,
  useMarkAllInboxRead,
  useMarkInboxRead,
  type InboxEventType,
  type InboxItem,
} from '../api/inbox'
import { useAuth } from '../auth/AuthProvider'
import { NotificationVisual } from '../components/NotificationBell'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { relativeTime } from '../components/ui/time'

/**
 * The full notification history. The bell shows the newest few; this is where somebody goes to
 * catch up on a week away, filter down to one kind of event, or empty the lot.
 */
export default function NotificationsPage() {
  const navigate = useNavigate()
  const { can } = useAuth()
  const isAdmin = can('Admin')

  const [unreadOnly, setUnreadOnly] = useState(false)
  const [category, setCategory] = useState<string | null>(null)

  const { data, isLoading, fetchNextPage, hasNextPage, isFetchingNextPage } = useInbox({ unreadOnly })
  const markRead = useMarkInboxRead()
  const markAll = useMarkAllInboxRead()
  const dismiss = useDismissInbox()
  const clear = useClearInbox()

  const categories = INBOX_CATEGORIES.filter((c) => !c.adminOnly || isAdmin)

  // Filtered client-side rather than through the endpoint's `type` parameter: a category is several
  // types, and the endpoint takes one. Paging still comes from the server, so this narrows the page
  // in hand rather than the feed — which is the honest behaviour for a "show me only downloads"
  // chip over an infinite list.
  const wanted = category
    ? new Set<InboxEventType>(categories.find((c) => c.label === category)?.types ?? [])
    : null

  const all = data?.pages.flatMap((p) => p.items) ?? []
  const items = wanted ? all.filter((i) => wanted.has(i.type)) : all
  const unread = data?.pages[0]?.unread ?? 0

  function open(item: InboxItem) {
    if (!item.read) markRead.mutate(item.id)
    if (item.url) navigate(item.url)
  }

  return (
    <>
      <PageHeader
        title="Notifications"
        description="What happened in your library while you were away."
        actions={
          <>
            <Tooltip label="Notification settings" withArrow>
              <ActionIcon
                component={Link}
                to="/settings?tab=account&s=notification-prefs"
                variant="subtle"
                color="gray"
                aria-label="Notification settings"
              >
                <IconSettings size={18} />
              </ActionIcon>
            </Tooltip>
            <Button
              variant="light"
              size="xs"
              disabled={unread === 0}
              onClick={() => markAll.mutate()}
            >
              Mark all read
            </Button>
            <Button
              variant="subtle"
              color="red"
              size="xs"
              disabled={all.length === 0}
              onClick={() => clear.mutate()}
            >
              Clear all
            </Button>
          </>
        }
      />

      <Group gap="xs" mb="md" wrap="wrap">
        <Chip.Group value={category} onChange={(v) => setCategory(v as string | null)}>
          <Group gap={6}>
            {categories.map((c) => (
              <Chip key={c.label} value={c.label} size="xs" variant="light">
                {c.label}
              </Chip>
            ))}
          </Group>
        </Chip.Group>
        <Switch
          size="xs"
          ml="auto"
          label="Unread only"
          checked={unreadOnly}
          onChange={(e) => setUnreadOnly(e.currentTarget.checked)}
        />
      </Group>

      {isLoading ? (
        <Group justify="center" py="xl">
          <Loader />
        </Group>
      ) : items.length === 0 ? (
        <EmptyState
          icon={IconBellOff}
          title={unreadOnly || category ? 'Nothing matches' : 'No notifications yet'}
          description={
            unreadOnly || category
              ? 'Try clearing the filters.'
              : 'New chapters, finished downloads and unlocked achievements land here.'
          }
        />
      ) : (
        <Card withBorder p={0} radius="md">
          <Stack gap={0}>
            {items.map((item) => (
              <Row key={item.id} item={item} onOpen={open} onDismiss={() => dismiss.mutate(item.id)} />
            ))}
          </Stack>
        </Card>
      )}

      {hasNextPage && (
        <Group justify="center" mt="md">
          <Button variant="subtle" size="xs" loading={isFetchingNextPage} onClick={() => void fetchNextPage()}>
            Load more
          </Button>
        </Group>
      )}
    </>
  )
}

function Row({
  item,
  onOpen,
  onDismiss,
}: {
  item: InboxItem
  onOpen: (item: InboxItem) => void
  onDismiss: () => void
}) {
  return (
    <Group
      gap={0}
      wrap="nowrap"
      align="stretch"
      style={{ borderBottom: '1px solid var(--mantine-color-default-border)' }}
    >
      <UnstyledButton
        onClick={() => onOpen(item)}
        px="md"
        py="sm"
        style={{
          flex: 1,
          minWidth: 0,
          borderLeft: `2px solid ${item.read ? 'transparent' : 'var(--mantine-color-brand-6)'}`,
        }}
        className="inbox-row"
      >
        <Group gap="sm" wrap="nowrap" align="flex-start">
          <Box mt={2}>
            <NotificationVisual item={item} size={34} />
          </Box>
          <Stack gap={2} style={{ minWidth: 0 }}>
            <Text size="sm" fw={item.read ? 500 : 650}>
              {item.title}
            </Text>
            <Text size="xs" c="dimmed">
              {item.body}
            </Text>
            <Text fz={10} lh={1.5} c="dimmed">
              {relativeTime(item.createdAt)}
            </Text>
          </Stack>
        </Group>
      </UnstyledButton>
      <Tooltip label="Dismiss" withArrow>
        <ActionIcon
          variant="subtle"
          color="gray"
          aria-label="Dismiss notification"
          onClick={onDismiss}
          m="sm"
        >
          <IconX size={15} />
        </ActionIcon>
      </Tooltip>
    </Group>
  )
}

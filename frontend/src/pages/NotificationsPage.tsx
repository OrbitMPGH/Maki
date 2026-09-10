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
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { relativeTime } from '../components/ui/time'

type NotificationDateGroup = {
  key: string
  label: string
  items: InboxItem[]
}

const NOTIFICATION_DATE_FORMAT = new Intl.DateTimeFormat(undefined, {
  weekday: 'long',
  month: 'long',
  day: 'numeric',
  year: 'numeric',
})

function notificationDateKey(createdAt: string): string {
  const date = new Date(createdAt)
  if (Number.isNaN(date.getTime())) return 'unknown'
  return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`
}

function notificationDateLabel(createdAt: string): string {
  const date = new Date(createdAt)
  return Number.isNaN(date.getTime()) ? 'Unknown date' : NOTIFICATION_DATE_FORMAT.format(date)
}

function groupNotifications(items: InboxItem[]): NotificationDateGroup[] {
  const groups: NotificationDateGroup[] = []
  const byKey = new Map<string, NotificationDateGroup>()

  for (const item of items) {
    const key = notificationDateKey(item.createdAt)
    let group = byKey.get(key)
    if (!group) {
      group = { key, label: notificationDateLabel(item.createdAt), items: [] }
      byKey.set(key, group)
      groups.push(group)
    }
    group.items.push(item)
  }

  return groups
}

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
  const groups = groupNotifications(items)
  const unread = data?.pages[0]?.unread ?? 0

  function open(item: InboxItem) {
    if (!item.read) markRead.mutate(item.id)
    if (item.url) navigate(item.url)
  }

  return (
    <SurfaceFrame pageStyle="operational" className="notifications-surface">
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

      <Group className="notifications-filter-rail" gap="xs" mb="md" wrap="wrap">
        <Chip.Group value={category} onChange={(v) => setCategory(v as string | null)}>
          <Group className="notifications-filter-controls" gap={6}>
            {categories.map((c) => (
              <Chip key={c.label} value={c.label} size="xs" variant="light">
                {c.label}
              </Chip>
            ))}
          </Group>
        </Chip.Group>
        <Switch
          className="notifications-unread-switch"
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
          variant={unreadOnly || category ? 'filtered' : 'quiet'}
          title={unreadOnly || category ? 'Nothing matches' : 'No notifications yet'}
          description={
            unreadOnly || category
              ? 'Try clearing the filters.'
              : 'New chapters, finished downloads and unlocked achievements land here.'
          }
        />
      ) : (
        <Card className="notifications-feed" withBorder p={0} radius="md">
          <Stack gap={0}>
            {groups.map((group) => (
              <section
                className="notification-date-group"
                key={group.key}
                aria-labelledby={`notification-date-${group.key}`}
              >
                <Text
                  className="notification-date-label"
                  component="h2"
                  id={`notification-date-${group.key}`}
                >
                  {group.label}
                </Text>
                {group.items.map((item) => (
                  <Row key={item.id} item={item} onOpen={open} onDismiss={() => dismiss.mutate(item.id)} />
                ))}
              </section>
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
    </SurfaceFrame>
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
      className="inbox-record"
      data-read={item.read ? 'true' : 'false'}
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
        }}
        className="inbox-row"
        data-read={item.read ? 'true' : 'false'}
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

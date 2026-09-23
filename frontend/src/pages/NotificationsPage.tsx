import {
  ActionIcon,
  Box,
  Button,
  Group,
  Stack,
  Skeleton,
  Switch,
  Tabs,
  Text,
  Tooltip,
  UnstyledButton,
} from '@mantine/core'
import { IconSettings, IconX } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { msg } from '@lingui/core/macro'
import type { I18n } from '@lingui/core'
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
import { useLabel } from '../i18n-context'
import { NotificationVisual } from '../components/NotificationBell'
import { ConfirmDialog } from '../components/ui/ConfirmDialog'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { relativeTime } from '../components/ui/time'
import { formatTime } from '../format'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

type NotificationDateGroup = {
  key: string
  label: string
  items: InboxItem[]
}

function notificationDateKey(createdAt: string): string {
  const date = new Date(createdAt)
  if (Number.isNaN(date.getTime())) return 'unknown'
  return `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`
}

const UNKNOWN_DATE = msg`Unknown date`
const TODAY = msg`Today`
const YESTERDAY = msg`Yesterday`

// Today / Yesterday get their own words; anything older falls back to the locale's own
// weekday-or-date formatting so we never hand-roll date math across fourteen languages.
function notificationDateLabel(createdAt: string, i18n: I18n): string {
  const date = new Date(createdAt)
  if (Number.isNaN(date.getTime())) return i18n._(UNKNOWN_DATE)

  const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()
  const today = startOfDay(new Date())
  const day = startOfDay(date)
  const diffDays = Math.round((today - day) / 86_400_000)

  if (diffDays === 0) return i18n._(TODAY)
  if (diffDays === 1) return i18n._(YESTERDAY)

  const format: Intl.DateTimeFormatOptions =
    diffDays > 0 && diffDays < 7
      ? { weekday: 'long' }
      : { weekday: 'long', month: 'long', day: 'numeric', year: 'numeric' }
  return new Intl.DateTimeFormat(i18n.locale, format).format(date)
}

function groupNotifications(items: InboxItem[], i18n: I18n): NotificationDateGroup[] {
  const groups: NotificationDateGroup[] = []
  const byKey = new Map<string, NotificationDateGroup>()

  for (const item of items) {
    const key = notificationDateKey(item.createdAt)
    let group = byKey.get(key)
    if (!group) {
      group = { key, label: notificationDateLabel(item.createdAt, i18n), items: [] }
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
  const { t, i18n } = useLingui()
  const navigate = useNavigate()
  const { can } = useAuth()
  const renderLabel = useLabel()
  const isAdmin = can('Admin')

  const [unreadOnly, setUnreadOnly] = useState(false)
  const [category, setCategory] = useState<string | null>(null)
  const [clearing, setClearing] = useState(false)

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
    ? new Set<InboxEventType>(categories.find((c) => c.id === category)?.types ?? [])
    : null

  const all = data?.pages.flatMap((p) => p.items) ?? []
  const items = wanted ? all.filter((i) => wanted.has(i.type)) : all
  const groups = groupNotifications(items, i18n)
  const unread = data?.pages[0]?.unread ?? 0

  function open(item: InboxItem) {
    if (!item.read) markRead.mutate(item.id)
    if (item.url) navigate(item.url)
  }

  return (
    <SurfaceFrame pageStyle="operational" className="notifications-surface">
      <PageHeader
        compact
        title={t`Notifications`}
        description={t`What happened in your library while you were away.`}
        actions={
          <>
            <Tooltip label={t`Notification settings`} withArrow>
              <ActionIcon
                component={Link}
                to="/settings?tab=account&s=notification-prefs"
                variant="subtle"
                color="var(--neutral)"
                aria-label={t`Notification settings`}
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
              <Trans>Mark all read</Trans>
            </Button>
            <Button
              variant="subtle"
              color="var(--danger)"
              size="xs"
              disabled={all.length === 0}
              onClick={() => setClearing(true)}
            >
              <Trans>Clear all</Trans>
            </Button>
          </>
        }
      />

      <div className="notifications-filter-rail">
        <Tabs
          className="notifications-filter-controls"
          value={category ?? 'all'}
          onChange={(v) => setCategory(!v || v === 'all' ? null : v)}
          variant="unstyled"
          classNames={{ list: 'series-tabs', tab: 'series-tab' }}
        >
          <Tabs.List>
            <Tabs.Tab value="all">
              <Trans>All</Trans>
            </Tabs.Tab>
            {categories.map((c) => (
              <Tabs.Tab key={c.id} value={c.id}>
                {renderLabel(c.label)}
              </Tabs.Tab>
            ))}
          </Tabs.List>
        </Tabs>
        <Switch
          className="notifications-unread-switch"
          size="xs"
          ml="auto"
          label={t`Unread only`}
          checked={unreadOnly}
          onChange={(e) => setUnreadOnly(e.currentTarget.checked)}
        />
      </div>

      {isLoading ? (
        <FeedSkeleton />
      ) : items.length === 0 ? (
        <EmptyState
          title={unreadOnly || category ? t`Nothing matches` : t`No notifications yet`}
          description={
            unreadOnly || category
              ? t`Try clearing the filters.`
              : t`New chapters, finished downloads and unlocked achievements land here.`
          }
        />
      ) : (
        <Panel className="notifications-feed" p={0}>
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
        </Panel>
      )}

      {hasNextPage && (
        <Group justify="center" mt="md">
          <Button variant="subtle" size="xs" loading={isFetchingNextPage} onClick={() => void fetchNextPage()}>
            <Trans>Load more</Trans>
          </Button>
        </Group>
      )}

      <ConfirmDialog
        opened={clearing}
        onClose={() => setClearing(false)}
        title={<Trans>Clear all notifications?</Trans>}
        confirmLabel={<Trans>Clear all</Trans>}
        loading={clear.isPending}
        onConfirm={() => clear.mutate(undefined, { onSuccess: () => setClearing(false) })}
      >
        <Trans>Every notification goes, read and unread. This can't be undone.</Trans>
      </ConfirmDialog>
    </SurfaceFrame>
  )
}

function FeedSkeleton() {
  return (
    <Panel className="notifications-feed" p={0} aria-hidden>
      <Box px="md" pt="md" pb={4}>
        <Skeleton h={10} w={72} />
      </Box>
      {[64, 48, 72, 56, 44, 60].map((width, i) => (
        <Group key={i} className="inbox-record" gap="sm" wrap="nowrap" align="flex-start" px="md" py="sm">
          <Skeleton w={34} h={49} radius="sm" mt={2} style={{ flexShrink: 0 }} />
          <Stack gap={7} style={{ flex: 1 }} pt={3}>
            <Skeleton h={12} w={width * 5} maw="80%" />
            <Skeleton h={10} w={(width - 12) * 5} maw="70%" />
            <Skeleton h={8} w={52} />
          </Stack>
        </Group>
      ))}
    </Panel>
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
  const { t } = useLingui()
  return (
    <Group className="inbox-record" data-read={item.read ? 'true' : 'false'} gap={0} wrap="nowrap" align="stretch">
      <UnstyledButton
        onClick={() => onOpen(item)}
        px="md"
        py="sm"
        style={{ flex: 1, minWidth: 0 }}
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
            <Text size="xs" c="var(--ink-3)">
              {item.body}
            </Text>
            <Text fz={10} lh={1.5} c="var(--ink-3)">
              {/* Under an older day's heading the day is already said, and a relative time can
                  disagree with it ("yesterday" under Monday, 34 hours on), so those rows give the
                  clock time instead. */}
              {new Date(item.createdAt).toDateString() === new Date().toDateString()
                ? relativeTime(item.createdAt)
                : formatTime(item.createdAt)}
            </Text>
          </Stack>
        </Group>
      </UnstyledButton>
      <Tooltip label={t`Dismiss`} withArrow>
        <ActionIcon
          variant="subtle"
          color="var(--neutral)"
          aria-label={t`Dismiss notification`}
          onClick={onDismiss}
          m="sm"
        >
          <IconX size={15} />
        </ActionIcon>
      </Tooltip>
    </Group>
  )
}

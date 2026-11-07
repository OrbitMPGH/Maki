import {
  ActionIcon,
  Anchor,
  Box,
  Group,
  Popover,
  ScrollArea,
  Stack,
  Text,
  Tooltip,
  UnstyledButton,
  VisuallyHidden,
} from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import {
  IconAlertTriangle,
  IconBell,
  IconBellOff,
  IconBook,
  IconBooks,
  IconCircleArrowUp,
  IconCloudDownload,
  IconDatabase,
  IconDownload,
  IconFeather,
  IconFlag,
  IconFolder,
  IconHeartbeat,
  IconInbox,
  IconLink,
  IconLinkOff,
  IconListCheck,
  IconPackage,
  IconPencil,
  IconMessages,
  IconPuzzle,
  IconRestore,
  IconShieldLock,
  IconThumbDown,
  IconThumbUp,
  IconTrendingUp,
  IconTrophy,
  IconWand,
  type Icon,
} from '../lib/icons'
import { useLocation, useNavigate } from 'react-router-dom'
import {
  useInbox,
  useInboxPrefs,
  useInboxUnread,
  useMarkAllInboxRead,
  useMarkInboxRead,
  type InboxEventType,
  type InboxItem,
} from '../api/inbox'
import { CountIndicator } from './ui/CountIndicator'
import { AchievementBadge } from '../pages/stats/AchievementBadge'
import { relativeTime } from './ui/time'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'

/**
 * Bell over the in-app notification inbox.
 * <p>
 * The badge count is its own query so it is live before anything opens the dropdown; the feed is
 * only fetched once the popover is opened, which keeps a page load to one small count request.
 */
export function NotificationBell({ position = 'bottom-end' }: { position?: 'bottom-end' | 'top-start' }) {
  const { t } = useLingui()
  const [opened, { toggle, close }] = useDisclosure(false)
  const navigate = useNavigate()
  // The page has no sidebar item, so the bell is what marks it as the current place.
  const onPage = useLocation().pathname.startsWith('/notifications')

  const { data: unread } = useInboxUnread()
  useInboxPrefs()
  const { data, isPending } = useInbox(undefined, opened)
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
      position={position}
      withArrow
      shadow="md"
      // The bell lives in the fixed sidebar and header. An absolute dropdown rode the page's scroll
      // and was pulled back a frame later, so it jittered behind the bell.
      floatingStrategy="fixed"
      opened={opened}
      onChange={toggle}
    >
      <Popover.Target>
        <Tooltip
          label={count > 0 ? plural(count, { one: '# unread', other: '# unread' }) : t`Notifications`}
          withArrow
          disabled={opened}
        >
          <CountIndicator count={count}>
            <ActionIcon
              variant="subtle"
              color="var(--neutral)"
              className="notification-bell"
              data-active={onPage || undefined}
              aria-label={
                count > 0
                  ? plural(count, { one: 'Notifications, # unread', other: 'Notifications, # unread' })
                  : t`Notifications`
              }
              onClick={toggle}
            >
              <IconBell size={19} />
            </ActionIcon>
          </CountIndicator>
        </Tooltip>
      </Popover.Target>

      <Popover.Dropdown p={0} aria-labelledby={undefined} aria-label={t`Notifications`}>
        <Group justify="space-between" px="sm" py={8} wrap="nowrap">
          <Text fw={650} size="sm">
            <Trans>Notifications</Trans>
          </Text>
          {count > 0 && (
            <Anchor component="button" type="button" size="xs" onClick={() => markAll.mutate()}>
              <Trans>Mark all read</Trans>
            </Anchor>
          )}
        </Group>

        {isPending ? (
          <Text size="xs" c="var(--ink-3)" px="sm" pb="sm">
            <Trans>Loading…</Trans>
          </Text>
        ) : items.length === 0 ? (
          <Stack align="center" gap={6} px="sm" py="lg">
            <IconBellOff size={22} opacity={0.4} />
            <Text size="xs" c="var(--ink-3)">
              <Trans>Nothing yet</Trans>
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
            <Trans>See all notifications</Trans>
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
      className="inbox-row"
      data-read={String(item.read)}
    >
      <Group gap="xs" wrap="nowrap" align="flex-start">
        <Box mt={2}>
          <NotificationVisual item={item} size={26} />
        </Box>
        <Stack gap={2} style={{ minWidth: 0 }}>
          <Text size="xs" fw={item.read ? 500 : 650} lineClamp={1}>
            {!item.read && <UnreadMark />}
            {item.title}
          </Text>
          <Text size="xs" c="var(--ink-3)" lineClamp={2}>
            {item.body}
          </Text>
          {/* fz, not size: `size` takes a token ("xs"), and a raw number there resolves against
              --mantine-line-height-{n}, which does not exist and lands as line-height: 100px. */}
          <Text fz="var(--type-micro)" lh={1.5} c="var(--ink-4)">
            {relativeTime(item.createdAt, 'narrow')}
          </Text>
        </Stack>
      </Group>
    </UnstyledButton>
  )
}

const TYPE_ICONS: Record<InboxEventType, Icon> = {
  newChapterAvailable: IconBook,
  smartDownloadQueued: IconWand,
  chapterDownloaded: IconDownload,
  downloadFailed: IconDownload,
  achievementUnlocked: IconTrophy,
  levelUp: IconTrendingUp,
  requestSubmitted: IconInbox,
  requestApproved: IconThumbUp,
  requestRejected: IconThumbDown,
  requestEdited: IconPencil,
  healthIssue: IconHeartbeat,
  updateAvailable: IconCloudDownload,
  importFinished: IconFolder,
  backupFinished: IconDatabase,
  sourceMatchFinished: IconLink,
  importListFinished: IconListCheck,
  followedCreatorRelease: IconFeather,
  chapterUpgraded: IconCircleArrowUp,
  upgradeRestoreFailed: IconRestore,
  torrentProposalPending: IconPackage,
  volumeUpgraded: IconBooks,
  accountSecurity: IconShieldLock,
  seriesStatusChanged: IconFlag,
  customSourceUpdated: IconPuzzle,
  noteReply: IconMessages,
  chapterDelisted: IconLinkOff,
}

/** A warning or error shows its severity; anything else shows what kind of event it was. */
export function LevelIcon({ level, type }: { level: InboxItem['level']; type: InboxEventType }) {
  if (level === 'error') return <IconAlertTriangle size={16} color="var(--danger)" />
  if (level === 'warning') return <IconAlertTriangle size={16} color="var(--warn)" />
  // Neutral, not the accent: under a red-leaning accent a brand-coloured icon reads as an error.
  const TypeIcon = TYPE_ICONS[type] ?? IconBell
  return <TypeIcon size={16} color="var(--ink-3)" />
}

/** Unread is said by a dot and the bolder title as well as the row's accent edge. */
export function UnreadMark() {
  const { t } = useLingui()
  return (
    <>
      <span className="inbox-unread-dot" aria-hidden />
      <VisuallyHidden>{t`Unread`} </VisuallyHidden>
    </>
  )
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
  if (item.badge) {
    return <AchievementBadge icon={item.badge.icon} tier={item.badge.tier} size={size < 30 ? 'sm' : 'md'} />
  }
  if (!item.coverUrl) {
    return <LevelIcon level={item.level} type={item.type} />
  }

  return (
    <Box className="inbox-cover" style={{ width: size, height: Math.round(size * 1.45) }}>
      <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />
      {item.level !== 'info' && (
        <span className="inbox-cover-badge">
          <IconAlertTriangle
            size={11}
            color={item.level === 'error' ? 'var(--danger)' : 'var(--warn)'}
          />
        </span>
      )}
    </Box>
  )
}

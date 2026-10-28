import { useEffect, useMemo } from 'react'
import { ActionIcon, Box, Button, Group, Popover, Progress, ScrollArea, Stack, Text, Tooltip, UnstyledButton } from '@mantine/core'
import { useDisclosure } from '@mantine/hooks'
import { IconDownload } from '@tabler/icons-react'
import { Link, useNavigate } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import { useQueue, useQueuePause, useQueueSummary } from '../api/hooks'
import { isHeldByPause, queueItemLabel } from '../api/queue'
import type { QueueItemDto } from '../api/types'
import { useLabel } from '../i18n-context'
import { useSourceLabel } from '../sourceLabels'
import { CountIndicator } from './ui/CountIndicator'
import { isQueueActive, needsImportReview, queueStatusVisual } from './ui/status'

const WORKING = new Set(['FetchingPages', 'Downloading', 'Validating', 'Packaging', 'Importing'])
const MAX_ROWS = 8

/**
 * The shell's Activity icon. With nothing in flight it is a plain link to `/activity`; while
 * something is downloading or waiting on an import decision it opens a dropdown of those items
 * instead, with the page one click further.
 */
export function ActivityButton({ position = 'bottom-end' }: { position?: 'bottom-end' | 'top-start' }) {
  const { t } = useLingui()
  const [opened, { toggle, close }] = useDisclosure(false)
  const { data: summary } = useQueueSummary()
  const active = summary?.active ?? 0
  // A download waiting on an import decision outranks work in progress: progress finishes on its
  // own, this does not, and the count is the only thing telling anyone it is there.
  const review = summary?.awaitingImport ?? 0
  const count = review > 0 ? review : active
  const hasActivity = active > 0 || review > 0

  useEffect(() => {
    if (!hasActivity) close()
  }, [hasActivity, close])

  const accessibleName =
    review > 0
      ? plural(review, {
          one: 'Activity, # download waiting for an import decision',
          other: 'Activity, # downloads waiting for an import decision',
        })
      : active > 0
        ? plural(active, {
            one: 'Activity, # download in progress',
            other: 'Activity, # downloads in progress',
          })
        : t`Activity`

  const icon = (
    <Tooltip
      label={
        review > 0 ? (
          <Plural value={review} one="# download waiting for an import decision"
            other="# downloads waiting for an import decision" />
        ) : active > 0 ? (
          <Plural value={active} one="# download in progress" other="# downloads in progress" />
        ) : (
          <Trans>Activity</Trans>
        )
      }
      withArrow
      disabled={opened}
    >
      <CountIndicator count={count} tone={review > 0 ? 'warn' : 'brand'}>
        {hasActivity ? (
          <ActionIcon variant="subtle" color="var(--neutral)" aria-label={accessibleName} onClick={toggle}>
            <IconDownload size={19} />
          </ActionIcon>
        ) : (
          <ActionIcon component={Link} to="/activity" variant="subtle" color="var(--neutral)" aria-label={accessibleName}>
            <IconDownload size={19} />
          </ActionIcon>
        )}
      </CountIndicator>
    </Tooltip>
  )

  return (
    <Popover
      width={360}
      position={position}
      withArrow
      shadow="md"
      // Same reason as the bell: the icon sits in the fixed sidebar and header.
      floatingStrategy="fixed"
      opened={opened && hasActivity}
      onChange={toggle}
    >
      <Popover.Target>{icon}</Popover.Target>
      <Popover.Dropdown p={0} aria-labelledby={undefined} aria-label={t`Activity`}>
        <ActivityDropdown active={active} onClose={close} />
      </Popover.Dropdown>
    </Popover>
  )
}

/** Its own component so the queue page and pause state are only fetched while the dropdown is open. */
function ActivityDropdown({ active, onClose }: { active: number; onClose: () => void }) {
  const navigate = useNavigate()
  const { data: queue, isPending } = useQueue(1, 200, true, true)
  const pause = useQueuePause().data

  const items = useMemo(() => {
    const live = (queue?.items ?? []).filter((q) => isQueueActive(q.status) || needsImportReview(q.status))
    const rank = (q: QueueItemDto) => (WORKING.has(q.status) ? 0 : needsImportReview(q.status) ? 1 : 2)
    return [...live].sort((a, b) => rank(a) - rank(b))
  }, [queue])
  const shown = items.slice(0, MAX_ROWS)
  const rest = items.length - shown.length

  function go(path: string) {
    onClose()
    navigate(path)
  }

  return (
    <>
      <Group justify="space-between" px="sm" py={8} wrap="nowrap">
        <Text fw={650} size="sm">
          <Trans>Activity</Trans>
        </Text>
        {active > 0 && (
          <Text size="xs" c="var(--ink-3)" className="tnum">
            <Plural value={active} one="# in progress" other="# in progress" />
          </Text>
        )}
      </Group>

      {isPending ? (
        <Text size="xs" c="var(--ink-3)" px="sm" pb="sm">
          <Trans>Loading…</Trans>
        </Text>
      ) : shown.length === 0 ? (
        <Text size="xs" c="var(--ink-3)" px="sm" pb="sm">
          <Trans>Nothing downloading right now</Trans>
        </Text>
      ) : (
        <ScrollArea.Autosize mah={380} type="auto">
          <Stack gap={0}>
            {shown.map((item) => (
              <ActivityRow
                key={item.id}
                item={item}
                held={isHeldByPause(pause, item.sourceName)}
                onOpen={() => go(`/series/${item.seriesId}`)}
              />
            ))}
          </Stack>
          {rest > 0 && (
            <Text size="xs" c="var(--ink-3)" px="sm" pb={8}>
              <Plural value={rest} one="and # more" other="and # more" />
            </Text>
          )}
        </ScrollArea.Autosize>
      )}

      <Box px="sm" py={8} style={{ borderTop: '1px solid var(--mantine-color-default-border)' }}>
        <Button size="xs" variant="light" fullWidth onClick={() => go('/activity')}>
          <Trans>Open Activity</Trans>
        </Button>
      </Box>
    </>
  )
}

function ActivityRow({
  item,
  held,
  onOpen,
}: {
  item: QueueItemDto
  held: boolean
  onOpen: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const sourceLabel = useSourceLabel()
  const working = WORKING.has(item.status)
  const waiting = item.status === 'Queued' || item.status === 'RateLimited'
  const status = waiting && held ? t`Paused` : renderLabel(queueStatusVisual(item.status).label)
  const source = item.status === 'Resolving' ? null : sourceLabel(item.sourceName)

  return (
    <UnstyledButton onClick={onOpen} px="sm" py={8} className="inbox-row">
      <Group gap="xs" wrap="nowrap" align="flex-start">
        <Box className="inbox-cover" data-series-cover={item.seriesId} mt={2} style={{ width: 26, height: 38 }}>
          {item.seriesCoverUrl && <img src={item.seriesCoverUrl} alt="" loading="lazy" decoding="async" />}
        </Box>
        <Stack gap={2} style={{ minWidth: 0, flex: 1 }}>
          <Text size="xs" fw={650} lineClamp={1}>
            {item.seriesTitle}
          </Text>
          <Text size="xs" c="var(--ink-3)" lineClamp={1}>
            {source ? `${queueItemLabel(item)} · ${source}` : queueItemLabel(item)}
          </Text>
          {working && item.pagesTotal > 0 ? (
            <Group gap="xs" wrap="nowrap">
              <Progress
                value={(item.pagesDone / item.pagesTotal) * 100}
                style={{ flex: 1 }}
                size="sm"
                radius="xl"
                animated={item.status === 'Downloading'}
                aria-label={t`Download progress`}
              />
              <Text fz="var(--type-micro)" lh={1.5} c="var(--ink-3)" className="tnum">
                {item.pagesDone}/{item.pagesTotal}
              </Text>
            </Group>
          ) : (
            <Text fz="var(--type-micro)" lh={1.5} c={needsImportReview(item.status) ? 'var(--warn)' : 'var(--ink-4)'}>
              {status}
            </Text>
          )}
        </Stack>
      </Group>
    </UnstyledButton>
  )
}

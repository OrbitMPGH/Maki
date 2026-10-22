import { errorText } from '../api/errorText'
import { useEffect, useState } from 'react'
import {
  Alert,
  Anchor,
  Box,
  Button,
  Group,
  Popover,
  ScrollArea,
  Skeleton,
  Stack,
  Table,
  Text,
  TextInput,
  Tooltip,
  VisuallyHidden,
} from '@mantine/core'
import { IconHistory, IconListDetails, IconRefresh, IconUserQuestion } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../auth/AuthProvider'
import { PageHeader } from '../components/ui/PageHeader'
import { SectionHeader } from '../components/ui/SectionHeader'
import { SeriesCell } from '../components/SeriesCell'
import { EmptyState } from '../components/ui/EmptyState'
import { Panel } from '../components/ui/Panel'
import { TableSkeleton } from '../components/ui/TableSkeleton'
import { statusToken, trackerConnectionVisual, trackerStatusVisual } from '../components/ui/status'
import { StatusDot } from '../components/ui/StatusDot'
import { useLabel } from '../i18n-context'
import { TagChip } from '../components/ui/TagChip'
import {
  useScrobbleAuthStart,
  useScrobbleDisconnect,
  useScrobbleIgnore,
  useScrobbleMatch,
  useScrobbleStatus,
  useScrobbleSyncNow,
  type ScrobbleConnection,
  type ScrobbleSyncRow,
  type ScrobbleUnmatchedItem,
} from '../api/hooks'
import { formatDateTime } from '../format'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { MetadataSiteIcon } from '../components/MetadataSiteIcon'

const TRACKER_SETTINGS = '/settings?tab=integrations&s=scrobbling'
const KAVITA_SETTINGS = '/settings?tab=integrations&s=kavita'

function fmtTime(iso: string | null | undefined): string {
  return iso ? formatDateTime(iso) : '-'
}

/** Appends a counter only to keys that collide, so a unique key never carries a positional suffix. */
function dedupeKeys(keys: string[]): string[] {
  const seen = new Map<string, number>()
  return keys.map((key) => {
    const count = (seen.get(key) ?? 0) + 1
    seen.set(key, count)
    return count === 1 ? key : `${key}#${count}`
  })
}

type SyncOutcome = { chapter: number; volume: number; status: string | null; services: string[] }

/**
 * One entry per series, newest first. The result most trackers share is the series' main line; any
 * tracker that landed somewhere else, or failed, is listed under it as an exception. The rows are
 * already one per series and tracker (sync state, not history), so nothing older is dropped.
 */
function groupBySeries(rows: ScrobbleSyncRow[], connections: ScrobbleConnection[]) {
  const order = (service: string) => connections.findIndex((c) => c.service === service)
  const byOrder = (a: string, b: string) => order(a) - order(b)
  const groups = new Map<
    string,
    {
      title: string
      at: string
      seriesId: number | null
      coverUrl: string | null
      seen: Set<string>
      outcomes: Map<string, SyncOutcome>
      failures: ScrobbleSyncRow[]
    }
  >()
  for (const r of rows) {
    const groupKey = r.seriesId === null ? `title:${r.title}` : `series:${r.seriesId}`
    let group = groups.get(groupKey)
    if (!group) {
      group = {
        title: r.title,
        at: r.at,
        seriesId: r.seriesId,
        coverUrl: r.coverUrl,
        seen: new Set(),
        outcomes: new Map(),
        failures: [],
      }
      groups.set(groupKey, group)
    }
    if (group.seen.has(r.service)) continue
    group.seen.add(r.service)
    if (r.error) {
      group.failures.push(r)
      continue
    }
    const key = `${r.chapter}|${r.volume}|${r.status ?? ''}`
    const outcome = group.outcomes.get(key)
    if (outcome) outcome.services.push(r.service)
    else group.outcomes.set(key, { chapter: r.chapter, volume: r.volume, status: r.status, services: [r.service] })
  }
  // Trackers in the Connections order everywhere, so the same icons sit in the same place row to row.
  return [...groups.entries()].map(([key, { title, at, seriesId, coverUrl, outcomes, failures }]) => {
    const lines = [...outcomes.values()]
    for (const line of lines) line.services.sort(byOrder)
    lines.sort((a, b) => b.chapter - a.chapter || byOrder(a.services[0], b.services[0]))
    const main = lines.reduce<SyncOutcome | null>(
      (best, line) => (!best || line.services.length > best.services.length ? line : best),
      null,
    )
    failures.sort((a, b) => byOrder(a.service, b.service))
    return { key, title, at, seriesId, coverUrl, main, others: lines.filter((line) => line !== main), failures }
  })
}

/** Brand glyph and colour per tracker, keyed by the scrobble service name. */
const TRACKER_ICONS: Record<string, { site: string; monogram: string; color: string }> = {
  anilist: { site: 'anilist', monogram: 'AL', color: 'var(--mantine-color-blue-5)' },
  mal: { site: 'myanimelist', monogram: 'MAL', color: 'var(--mantine-color-indigo-4)' },
  kitsu: { site: 'kitsu', monogram: 'KI', color: 'var(--mantine-color-orange-5)' },
  mangabaka: { site: 'mangabaka', monogram: 'MB', color: 'var(--mantine-color-orange-5)' },
  mangaupdates: { site: 'mangaupdates', monogram: 'MU', color: 'var(--mantine-color-grape-5)' },
  kavita: { site: 'kavita', monogram: 'K', color: 'var(--mantine-color-teal-5)' },
}

function TrackerIcon({ service, size = 14 }: { service: string; size?: number }) {
  const icon = TRACKER_ICONS[service] ?? {
    site: service,
    monogram: service.slice(0, 2).toUpperCase(),
    color: 'var(--ink-3)',
  }
  return (
    <span className="scrobble-tracker-icon" style={{ color: icon.color }} aria-hidden>
      <MetadataSiteIcon site={icon.site} monogram={icon.monogram} size={size} />
    </span>
  )
}

/** A row of tracker icons, each named on hover and to a screen reader. */
function TrackerMarks({ services, connections }: { services: string[]; connections: ScrobbleConnection[] }) {
  return (
    <span className="scrobble-marks">
      {services.map((service) => {
        const label = serviceLabel(connections, service)
        return (
          <Tooltip key={service} label={label} withArrow openDelay={150}>
            <span className="scrobble-mark" role="img" aria-label={label}>
              <TrackerIcon service={service} size={13} />
            </span>
          </Tooltip>
        )
      })}
    </span>
  )
}

function Progress({ outcome }: { outcome: SyncOutcome }) {
  return (
    <Text span size="sm" fw={600} className="tnum">
      {outcome.volume ? (
        <Trans>
          ch. {outcome.chapter} · vol. {outcome.volume}
        </Trans>
      ) : (
        <Trans>ch. {outcome.chapter}</Trans>
      )}
    </Text>
  )
}

function TrackerStatus({ status }: { status: string }) {
  const renderLabel = useLabel()
  const visual = trackerStatusVisual(status)
  return <StatusDot tone={statusToken(visual.color)}>{renderLabel(visual.label)}</StatusDot>
}

/**
 * Every line shares two columns, what (chapter and status) and who (trackers), so an exception's
 * chapter sits right under the main one and reads as a comparison rather than more text.
 */
function RecentSyncResult({
  group,
  connections,
}: {
  group: ReturnType<typeof groupBySeries>[number]
  connections: ScrobbleConnection[]
}) {
  const { main, others, failures } = group
  const names = (services: string[]) => services.map((s) => serviceLabel(connections, s)).join(', ')
  const count = main?.services.length ?? 0
  const everyone = others.length === 0 && failures.length === 0

  return (
    <div className="scrobble-result">
      {main && (
        <div className="scrobble-result-line">
          <span className="scrobble-result-what">
            <Progress outcome={main} />
            {main.status && <TrackerStatus status={main.status} />}
          </span>
          <span className="scrobble-result-who">
            <TrackerMarks services={main.services} connections={connections} />
            <Text span size="sm" c="var(--ink-3)">
              {count === 1 ? (
                names(main.services)
              ) : everyone ? (
                <Plural value={count} _2="Both trackers" other="All # trackers" />
              ) : (
                <Plural value={count} one="# tracker" other="# trackers" />
              )}
            </Text>
          </span>
        </div>
      )}
      {(others.length > 0 || failures.length > 0) && (
        <ul className="scrobble-exceptions" data-flat={main ? undefined : true}>
          {others.map((o) => {
            const diff = main ? Math.round((main.chapter - o.chapter) * 10) / 10 : 0
            return (
              <li key={o.services.join()} className="scrobble-result-line" data-exception>
                <span className="scrobble-result-what">
                  <Text span size="sm" c="var(--ink-2)" className="tnum">
                    {o.volume ? (
                      <Trans>
                        ch. {o.chapter} · vol. {o.volume}
                      </Trans>
                    ) : (
                      <Trans>ch. {o.chapter}</Trans>
                    )}
                  </Text>
                  {diff !== 0 && (
                    <Text span size="xs" c="var(--ink-3)" className="tnum">
                      {diff > 0 ? (
                        <Plural value={diff} other="# behind" />
                      ) : (
                        <Plural value={-diff} other="# ahead" />
                      )}
                    </Text>
                  )}
                  {o.status && o.status !== main?.status && <TrackerStatus status={o.status} />}
                </span>
                <span className="scrobble-result-who">
                  <TrackerMarks services={o.services} connections={connections} />
                  <Text span size="sm" c="var(--ink-3)">
                    {names(o.services)}
                  </Text>
                </span>
              </li>
            )
          })}
          {failures.map((r) => (
            <FailureLine key={r.service} service={r.service} error={r.error ?? ''} connections={connections} />
          ))}
        </ul>
      )}
    </div>
  )
}

/**
 * A sync a tracker refused. The tracker's own message is English, technical and often long (an HTTP
 * verb and path, a timestamp), so it waits behind Details instead of filling the row.
 */
function FailureLine({
  service,
  error,
  connections,
}: {
  service: string
  error: string
  connections: ScrobbleConnection[]
}) {
  const [open, setOpen] = useState(false)
  return (
    <li className="scrobble-result-line" data-exception>
      <span className="scrobble-result-what">
        <Text span size="sm" fw={600} c="var(--danger)">
          <Trans>Not updated</Trans>
        </Text>
      </span>
      <span className="scrobble-result-who">
        <TrackerMarks services={[service]} connections={connections} />
        <Text span size="sm" c="var(--ink-3)">
          {serviceLabel(connections, service)}
        </Text>
        {error && (
          <Button size="compact-xs" variant="subtle" color="var(--neutral)" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
            {open ? <Trans>Hide details</Trans> : <Trans>Details</Trans>}
          </Button>
        )}
      </span>
      {open && <p className="scrobble-result-detail">{error}</p>}
    </li>
  )
}

/** A tracker's display name from its connection card, falling back to the raw service key. */
function serviceLabel(connections: ScrobbleConnection[] | undefined, service: string): string {
  return connections?.find((c) => c.service === service)?.label ?? service
}

/**
 * One tracker in the Connections row: its icon, name and state dot. The state in words and what to
 * do about it (connect, disconnect, check the saved details) open on click, so the row stays one line.
 */
function ConnectionChip({ connection }: { connection: ScrobbleConnection }) {
  const authStart = useScrobbleAuthStart()
  const disconnect = useScrobbleDisconnect()
  const { can } = useAuth()
  const isAdmin = can('Admin')
  const isKavita = connection.service === 'kavita'
  const ownDetails = connection.savedDetails != null
  const noDetails = connection.savedDetails === false

  const renderLabel = useLabel()
  const dot = trackerConnectionVisual(connection.connected, connection.configured)
  // Every state that is not connected says what to do next: connect, check the saved details, or
  // set the tracker up. Setting one up is an admin job, so a reader is told who can.
  const state = connection.connected ? (
    (connection.username ?? <Trans>Connected</Trans>)
  ) : connection.configured && connection.oAuth ? (
    <Trans>Not connected yet.</Trans>
  ) : connection.configured ? (
    isKavita ? (
      <Trans>Can't reach Kavita with the saved address and API key.</Trans>
    ) : noDetails ? (
      <Trans>Add your details in Settings.</Trans>
    ) : (
      <Trans>Can't sign in with the saved details.</Trans>
    )
  ) : isAdmin ? (
    <Trans>Not set up yet.</Trans>
  ) : (
    <Trans>Not set up yet. An admin can add it in Settings.</Trans>
  )
  const settingsLink =
    (isAdmin || ownDetails) && !connection.connected && !(connection.configured && connection.oAuth) ? (
      <Button component={Link} to={isKavita ? KAVITA_SETTINGS : TRACKER_SETTINGS} size="compact-sm" variant="default">
        {connection.configured && !noDetails ? <Trans>Check in Settings</Trans> : <Trans>Set up in Settings</Trans>}
      </Button>
    ) : null

  const connect = () => {
    authStart.mutate(connection.service, {
      onSuccess: (data) => {
        window.location.href = data.url
      },
    })
  }

  return (
    <Popover position="bottom-start" shadow="md" withArrow width={280}>
      <Popover.Target>
        <button type="button" className="tag-chip scrobble-connection" data-interactive>
          <TrackerIcon service={connection.service} size={14} />
          <span>{connection.label}</span>
          <VisuallyHidden>: {renderLabel(dot.label)}</VisuallyHidden>
          <span
            className="scrobble-connection-dot"
            style={{ background: `var(--${statusToken(dot.color)})` }}
            aria-hidden
          />
        </button>
      </Popover.Target>
      <Popover.Dropdown>
        <Text size="sm" c="var(--ink-3)" style={{ overflowWrap: 'anywhere' }}>
          {state}
        </Text>
        {connection.oAuth && connection.configured && (
          <Group mt="auto" pt="sm">
            {connection.connected ? (
              <Button
                size="compact-sm"
                variant="subtle"
                color="var(--neutral)"
                className="scrobble-disconnect"
                loading={disconnect.isPending}
                onClick={() =>
                  disconnect.mutate(connection.service)
                }
              >
                <Trans>Disconnect</Trans>
              </Button>
            ) : (
              <Button size="compact-sm" loading={authStart.isPending} onClick={connect}>
                <Trans>Connect</Trans>
              </Button>
            )}
          </Group>
        )}
        {settingsLink && (
          <Group mt="auto" pt="sm">
            {settingsLink}
          </Group>
        )}
      </Popover.Dropdown>
    </Popover>
  )
}

function UnmatchedCard({ item }: { item: ScrobbleUnmatchedItem }) {
  const { t } = useLingui()
  const match = useScrobbleMatch()
  const ignore = useScrobbleIgnore()
  const { data } = useScrobbleStatus()
  const [input, setInput] = useState('')

  const assign = (remoteId: string) => {
    if (!remoteId.trim()) return
    match.mutate(
      { kavitaSeriesId: item.kavitaSeriesId, service: item.service, remoteId: remoteId.trim() },
      {
        onSuccess: (data) => {
          notifications.show({ message: data.message, color: 'var(--ok)' })
          setInput('')
        },
      },
    )
  }

  const href = item.seriesId != null ? `/series/${item.seriesId}` : null
  const cover = item.coverUrl && <img src={item.coverUrl} alt="" loading="lazy" decoding="async" />
  return (
    <Panel p="md">
      <div className="cover-panel scrobble-review">
        {/* No library series matched this title, so there is no cover to show and no empty box either. */}
        {href && (
          <Link to={href} className="cover-panel-cover" tabIndex={-1} aria-hidden>
            {cover}
          </Link>
        )}
        <div className="cover-panel-body">
          <Group gap="xs">
            <StatusDot tone="warn" aria-hidden />
            {href ? (
              <Text component={Link} to={href} className="series-cell-title">
                {item.title}
              </Text>
            ) : (
              <Text className="series-cell-title">{item.title}</Text>
            )}
            <TagChip size="sm">{serviceLabel(data?.connections, item.service)}</TagChip>
          </Group>
          <Text size="sm" c="var(--ink-3)">
            {item.reason}
          </Text>
          {item.candidates.length > 0 && (
            <Stack gap={4} mt="xs">
              {item.candidates.map((c) => (
                <Group key={c.id} gap="xs">
                  <Button size="compact-xs" variant="light" onClick={() => assign(c.id)}>
                    <Trans>Use</Trans>
                  </Button>
                  <Text size="sm" component="a" href={c.url} target="_blank" rel="noopener" c="brand.4">
                    {c.title}
                  </Text>
                </Group>
              ))}
            </Stack>
          )}
          <Group mt="sm" gap="xs">
            <TextInput
              aria-label={t`Paste series URL or id…`}
              size="xs"
              style={{ flex: '1 1 12rem' }}
              placeholder={t`Paste series URL or id…`}
              value={input}
              onChange={(e) => setInput(e.currentTarget.value)}
              onKeyDown={(e) => e.key === 'Enter' && assign(input)}
            />
            <Button size="compact-sm" onClick={() => assign(input)} loading={match.isPending}>
              <Trans>Assign</Trans>
            </Button>
            <Button
              size="compact-sm"
              variant="default"
              loading={ignore.isPending}
              onClick={() =>
                ignore.mutate({ kavitaSeriesId: item.kavitaSeriesId, service: item.service })
              }
            >
              <Trans>Ignore</Trans>
            </Button>
          </Group>
        </div>
      </div>
    </Panel>
  )
}

export default function ScrobblePage() {
  const { t } = useLingui()
  const { data, error, isRefetching, refetch } = useScrobbleStatus()
  const syncNow = useScrobbleSyncNow()
  const [searchParams, setSearchParams] = useSearchParams()
  const { can } = useAuth()
  const isAdmin = can('Admin')

  // Surface the OAuth redirect result once, then clean the URL.
  useEffect(() => {
    const connected = searchParams.get('connected')
    const oauthError = searchParams.get('error')
    if (connected) {
      notifications.show({ message: now`${connected} connected`, color: 'var(--ok)' })
    }
    if (oauthError) {
      notifications.show({ message: oauthError, color: 'var(--danger)', autoClose: 10000 })
    }
    if (connected || oauthError) {
      setSearchParams({}, { replace: true })
    }
  }, [searchParams, setSearchParams])

  const anyTrackerConnected = data?.connections.some((c) => c.service !== 'kavita' && c.connected)
  const intervalMinutes = data?.intervalMinutes ?? 30
  const lastSync = fmtTime(data?.lastSyncAt)
  const nextSync = fmtTime(data?.nextSyncAt)

  const header = (
    <PageHeader
      compact
      title={t`Scrobble`}
      description={
        <Trans>
          Sends what you read to your trackers every{' '}
          <Plural value={intervalMinutes} one="# minute" other="# minutes" />. Progress on a tracker is never
          lowered.
        </Trans>
      }
      actions={
        <>
          {/* data-disabled rather than disabled: a disabled button gets no hover or focus, so the
              tooltip saying why could never show. */}
          <Tooltip label={t`Connect a tracker first`} disabled={anyTrackerConnected}>
            <Button
              leftSection={<IconRefresh size={16} />}
              loading={syncNow.isPending || data?.running}
              data-disabled={!anyTrackerConnected || undefined}
              aria-disabled={!anyTrackerConnected}
              onClick={() => {
                if (!anyTrackerConnected) return
                syncNow.mutate(undefined, {
                  onSuccess: (r) => notifications.show({ message: r.message }),
                })
              }}
            >
              <Trans>Sync now</Trans>
            </Button>
          </Tooltip>
        </>
      }
    />
  )

  const syncTimes = (
    <Text size="xs" c="var(--ink-3)" className="tnum">
      {data?.running ? (
        <Trans>
          sync running… · last {lastSync} · next {nextSync}
        </Trans>
      ) : (
        <Trans>
          last {lastSync} · next {nextSync}
        </Trans>
      )}
    </Text>
  )

  if (error && !data) {
    return (
      <SurfaceFrame pageStyle="operational">
        {header}
        <EmptyState
          title={t`Couldn't load scrobbling`}
          description={errorText(error)}
          actionLabel={isRefetching ? t`Retrying…` : t`Retry`}
          onAction={() => void refetch()}
        />
      </SurfaceFrame>
    )
  }

  return (
    <SurfaceFrame pageStyle="operational">
      {header}

      {error && (
        <Alert color="var(--danger)" variant="light" mb="md">
          {errorText(error)}
        </Alert>
      )}

      <div className="scrobble-connections" role="group" aria-label={t`Connections`}>
        {data
          ? data.connections.map((c) => <ConnectionChip key={c.service} connection={c} />)
          : Array.from({ length: 6 }, (_, i) => <Skeleton key={i} h={28} w={110} radius="xl" aria-hidden />)}
        {isAdmin && (
          <Anchor component={Link} to={TRACKER_SETTINGS} size="sm" className="scrobble-connections-setup">
            <Trans>Set up trackers</Trans>
          </Anchor>
        )}
      </div>

      <Stack gap="xl" mt="lg">

        <Stack gap="sm">
          <SectionHeader
            icon={IconUserQuestion}
            title={t`Needs review`}
            count={data && data.unmatched.length > 0 ? data.unmatched.length : undefined}
          />
          {!data ? (
            <Skeleton h={72} aria-hidden />
          ) : data.unmatched.length > 0 ? (
            <Stack gap="sm">
              {data.unmatched.map((u) => (
                <UnmatchedCard key={`${u.kavitaSeriesId}-${u.service}`} item={u} />
              ))}
            </Stack>
          ) : (
            <EmptyState compact mood="pleased" title={t`Nothing needs review.`} />
          )}
        </Stack>

        <Stack gap="sm">
          <SectionHeader icon={IconHistory} title={t`Recent syncs`} action={data ? syncTimes : undefined} />
          {!data ? (
            <TableSkeleton columns={3} rows={4} />
          ) : data.recent.length > 0 ? (
            <Panel p={0} className="table-panel">
              <Table.ScrollContainer minWidth={600}>
                <Table className="panel-table scrobble-recent-table" highlightOnHover>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>
                        <Trans>Series</Trans>
                      </Table.Th>
                      <Table.Th>
                        <Trans>Result</Trans>
                      </Table.Th>
                      <Table.Th data-priority="low">
                        <Trans>When</Trans>
                      </Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {groupBySeries(data.recent, data.connections).map((group) => (
                      <Table.Tr key={group.key}>
                        <Table.Td>
                          <SeriesCell
                            seriesId={group.seriesId}
                            title={group.title || '#'}
                            coverUrl={group.coverUrl}
                            small
                          />
                        </Table.Td>
                        <Table.Td>
                          <div className="scrobble-result-cell">
                            <RecentSyncResult group={group} connections={data.connections} />
                          </div>
                        </Table.Td>
                        <Table.Td data-priority="low">
                          <Text size="sm" c="var(--ink-3)">
                            {fmtTime(group.at)}
                          </Text>
                        </Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Table.ScrollContainer>
            </Panel>
          ) : (
            <EmptyState compact mood="asleep" title={t`No syncs yet.`} />
          )}
        </Stack>

        <Stack gap="sm">
          <SectionHeader icon={IconListDetails} title={t`Activity log`} />
          <Panel p={0}>
            <ScrollArea.Autosize mah={360}>
              {!data ? (
                <Box p="sm">
                  <Skeleton h={48} aria-hidden />
                </Box>
              ) : data.log.length > 0 ? (
                <ul className="scrobble-log">
                  {dedupeKeys(data.log.map((l) => `${l.timestamp}-${l.service}-${l.message}`)).map((key, i) => {
                    const l = data.log[i]
                    return (
                      <li key={key} className="scrobble-log-line" data-level={l.level}>
                        <span className="scrobble-log-time tnum">{fmtTime(l.timestamp)}</span>
                        <span className="scrobble-log-service">
                          {l.service && <TrackerMarks services={[l.service]} connections={data.connections} />}
                        </span>
                        <span className="scrobble-log-text">
                          {l.title && <span className="scrobble-log-title">{l.title}</span>}
                          {l.message}
                        </span>
                      </li>
                    )
                  })}
                </ul>
              ) : (
                <EmptyState compact mood="asleep" title={t`No activity yet.`} />
              )}
            </ScrollArea.Autosize>
          </Panel>
        </Stack>
      </Stack>
    </SurfaceFrame>
  )
}

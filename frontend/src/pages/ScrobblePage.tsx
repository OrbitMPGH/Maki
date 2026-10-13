import { errorText } from '../api/errorText'
import { useEffect, useState } from 'react'
import {
  Alert,
  Anchor,
  Badge,
  Box,
  Button,
  Group,
  ScrollArea,
  SimpleGrid,
  Skeleton,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core'
import { IconRefresh } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { Link, useSearchParams } from 'react-router-dom'
import { useAuth } from '../auth/AuthProvider'
import { PageHeader } from '../components/ui/PageHeader'
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

/** A tracker's display name from its connection card, falling back to the raw service key. */
/**
 * One entry per series, newest first, with each tracker's latest result under it. The rows are
 * already one per series and tracker (sync state, not history), so nothing older is dropped.
 */
function groupBySeries(rows: ScrobbleSyncRow[], connections: ScrobbleConnection[]) {
  const groups = new Map<string, { title: string; at: string; rows: ScrobbleSyncRow[] }>()
  for (const r of rows) {
    const group = groups.get(r.title)
    if (!group) groups.set(r.title, { title: r.title, at: r.at, rows: [r] })
    else if (!group.rows.some((x) => x.service === r.service)) group.rows.push(r)
  }
  // Trackers in the Connections order on every row, so a column of names lines up down the table.
  const order = (service: string) => connections.findIndex((c) => c.service === service)
  for (const group of groups.values()) group.rows.sort((a, b) => order(a.service) - order(b.service))
  return [...groups.values()]
}

function serviceLabel(connections: ScrobbleConnection[] | undefined, service: string): string {
  return connections?.find((c) => c.service === service)?.label ?? service
}

function ConnectionCard({ connection }: { connection: ScrobbleConnection }) {
  const authStart = useScrobbleAuthStart()
  const disconnect = useScrobbleDisconnect()
  const { can } = useAuth()
  const isAdmin = can('Admin')
  const isKavita = connection.service === 'kavita'

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
    ) : (
      <Trans>Can't sign in with the saved details.</Trans>
    )
  ) : isAdmin ? (
    <Trans>Not set up yet.</Trans>
  ) : (
    <Trans>Not set up yet. An admin can add it in Settings.</Trans>
  )
  const settingsLink = isAdmin && !connection.connected && !(connection.configured && connection.oAuth) ? (
    <Button component={Link} to={isKavita ? KAVITA_SETTINGS : TRACKER_SETTINGS} size="compact-sm" variant="default">
      {connection.configured ? <Trans>Check in Settings</Trans> : <Trans>Set up in Settings</Trans>}
    </Button>
  ) : null

  const connect = () => {
    authStart.mutate(connection.service, {
      onSuccess: (data) => {
        window.location.href = data.url
      },
    })
  }

  // A column with the buttons pushed to the bottom, so a state that wraps to two lines in one card
  // does not drop its button below the others in the row.
  return (
    <Panel p="md" style={{ display: 'flex', flexDirection: 'column' }}>
      <Group gap="xs">
        <Box
          w={10}
          h={10}
          style={{ borderRadius: '50%', background: `var(--${statusToken(dot.color)})` }}
        />
        <Text fw={700}>{connection.label}</Text>
      </Group>
      <Text size="sm" c="var(--ink-3)" mt={4} style={{ overflowWrap: 'anywhere' }}>
        {state}
      </Text>
      {connection.oAuth && connection.configured && (
        <Group mt="auto" pt="sm">
          {connection.connected ? (
            <Button
              size="compact-sm"
              variant="default"
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
    </Panel>
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

  return (
    <Panel edge="warn" p="md">
      <Group gap="xs">
        <Text fw={700}>{item.title}</Text>
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
          aria-label={t`Paste series URL or numeric id…`}
          size="xs"
          style={{ flex: '1 1 12rem' }}
          placeholder={t`Paste series URL or numeric id…`}
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
    </Panel>
  )
}

export default function ScrobblePage() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data, error, isRefetching, refetch } = useScrobbleStatus()
  const syncNow = useScrobbleSyncNow()
  const [searchParams, setSearchParams] = useSearchParams()

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
          Every <Plural value={intervalMinutes} one="# minute" other="# minutes" />, sends what you have
          read to your trackers: chapters finished in Maki's reader or an OPDS app, and in Kavita when it is
          connected. Remote progress is never lowered and completed entries are never demoted. Trackers are
          set up in{' '}
          <Anchor component={Link} to={TRACKER_SETTINGS}>
            Settings
          </Anchor>
          .
        </Trans>
      }
      actions={
        <Group gap="sm">
          <Text size="xs" c="var(--ink-3)" ta="right" className="tnum">
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
        </Group>
      }
    />
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

      <Title order={2} size="h4" mb="sm">
        <Trans>Connections</Trans>
      </Title>
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 5 }} mb="lg">
        {data
          ? data.connections.map((c) => <ConnectionCard key={c.service} connection={c} />)
          : Array.from({ length: 5 }, (_, i) => <Skeleton key={i} h={96} aria-hidden />)}
      </SimpleGrid>

      <Group gap="xs" mb="sm">
        <Title order={2} size="h4">
          <Trans>Needs review</Trans>
        </Title>
        {data && data.unmatched.length > 0 && (
          <Badge variant="light" color="var(--warn)">
            {data.unmatched.length}
          </Badge>
        )}
      </Group>
      {!data ? (
        <Skeleton h={72} mb="lg" aria-hidden />
      ) : data.unmatched.length > 0 ? (
        <Stack gap="sm" mb="lg">
          {data.unmatched.map((u) => (
            <UnmatchedCard key={`${u.kavitaSeriesId}-${u.service}`} item={u} />
          ))}
        </Stack>
      ) : (
        <Box mb="lg">
          <EmptyState compact mood="pleased" title={t`Nothing needs review.`} />
        </Box>
      )}

      <Title order={2} size="h4" mb="sm">
        <Trans>Recent syncs</Trans>
      </Title>
      {!data ? (
        <Box mb="lg">
          <TableSkeleton columns={5} rows={4} />
        </Box>
      ) : data.recent.length > 0 ? (
        <Panel p={0} className="table-panel" mb="lg">
          <Table.ScrollContainer minWidth={600}>
            <Table className="panel-table scrobble-recent-table" highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>
                    <Trans>Series</Trans>
                  </Table.Th>
                  <Table.Th>
                    <Trans>Trackers</Trans>
                  </Table.Th>
                  <Table.Th data-priority="low">
                    <Trans>When</Trans>
                  </Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {groupBySeries(data.recent, data.connections).map((group) => (
                  <Table.Tr key={group.title}>
                    <Table.Td className="scrobble-recent-series">{group.title || '#'}</Table.Td>
                    <Table.Td>
                      <ul className="scrobble-trackers">
                        {group.rows.map((r) => (
                          <li key={r.service} className="scrobble-tracker" data-failed={r.error ? true : undefined}>
                            <span className="scrobble-tracker-name">{serviceLabel(data.connections, r.service)}</span>
                            {r.error ? (
                              // The tracker's own words: English and technical, so they follow the
                              // plain statement rather than replace it.
                              <span className="scrobble-tracker-error">
                                <Text span size="sm" fw={600} c="var(--danger)">
                                  <Trans>Not updated</Trans>
                                </Text>
                                <Text span size="sm" c="var(--ink-3)" className="scrobble-tracker-reason">
                                  {r.error}
                                </Text>
                              </span>
                            ) : (
                              <>
                                <Text span size="sm" className="tnum">
                                  {r.volume ? (
                                    <Trans>
                                      ch. {r.chapter} · vol. {r.volume}
                                    </Trans>
                                  ) : (
                                    <Trans>ch. {r.chapter}</Trans>
                                  )}
                                </Text>
                                {r.status &&
                                  (() => {
                                    const visual = trackerStatusVisual(r.status)
                                    return <StatusDot tone={statusToken(visual.color)}>{renderLabel(visual.label)}</StatusDot>
                                  })()}
                              </>
                            )}
                          </li>
                        ))}
                      </ul>
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
        <Box mb="lg">
          <EmptyState compact mood="asleep" title={t`No syncs yet.`} />
        </Box>
      )}

      <Title order={2} size="h4" mb="sm">
        <Trans>Activity log</Trans>
      </Title>
      <Panel p="sm">
        <ScrollArea.Autosize mah={320}>
          {!data ? (
            <Skeleton h={48} aria-hidden />
          ) : data.log.length > 0 ? (
            <Stack gap={2}>
              {dedupeKeys(data.log.map((l) => `${l.timestamp}-${l.service}-${l.message}`)).map((key, i) => {
                const l = data.log[i]
                return (
                  // component="div": the line contains a Badge (a div), invalid inside <p>
                  <Text key={key} size="xs" ff="monospace" component="div">
                    <Text
                      span
                      c={
                        l.level === 'error'
                          ? 'var(--danger)'
                          : l.level === 'warning'
                            ? 'var(--warn)'
                            : 'var(--ink-3)'
                      }
                    >
                      {fmtTime(l.timestamp)}
                    </Text>{' '}
                    {l.service && (
                      <Badge size="xs" variant="light" mr={4}>
                        {serviceLabel(data.connections, l.service)}
                      </Badge>
                    )}
                    {l.title && <Text span fw={600}>{l.title} </Text>}
                    {l.message}
                  </Text>
                )
              })}
            </Stack>
          ) : (
            <EmptyState compact mood="asleep" title={t`No activity yet.`} />
          )}
        </ScrollArea.Autosize>
      </Panel>
    </SurfaceFrame>
  )
}

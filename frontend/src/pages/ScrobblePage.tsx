import { useEffect, useState } from 'react'
import {
  Alert,
  Badge,
  Box,
  Button,
  Group,
  ScrollArea,
  SimpleGrid,
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
import { useSearchParams } from 'react-router-dom'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
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
  type ScrobbleUnmatchedItem,
} from '../api/hooks'
import { formatDateTime } from '../format'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { onErrorToast } from '../lib/errors'

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
function serviceLabel(connections: ScrobbleConnection[] | undefined, service: string): string {
  return connections?.find((c) => c.service === service)?.label ?? service
}

function ConnectionCard({ connection }: { connection: ScrobbleConnection }) {
  const authStart = useScrobbleAuthStart()
  const disconnect = useScrobbleDisconnect()

  const dot = trackerConnectionVisual(connection.connected, connection.configured)
  const state = connection.connected ? (
    (connection.username ?? <Trans>connected</Trans>)
  ) : connection.configured ? (
    <Trans>configured, not connected</Trans>
  ) : (
    <Trans>not configured (see Settings)</Trans>
  )

  const connect = () => {
    authStart.mutate(connection.service, {
      onSuccess: (data) => {
        window.location.href = data.url
      },
    })
  }

  return (
    <Panel p="md">
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
        <Group mt="sm">
          {connection.connected ? (
            <Button
              size="compact-sm"
              variant="default"
              loading={disconnect.isPending}
              onClick={() =>
                disconnect.mutate(connection.service, {
                  onError: onErrorToast,
                })
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
        onError: onErrorToast,
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
            ignore.mutate(
              { kavitaSeriesId: item.kavitaSeriesId, service: item.service },
              { onError: onErrorToast },
            )
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
  const { data, error } = useScrobbleStatus()
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

  return (
    <SurfaceFrame pageStyle="operational">
      <PageHeader
        compact
        title={t`Scrobble`}
        description={
          <Trans>
            Reads reading progress from Kavita and pushes forward-only updates to your trackers every{' '}
            <Plural value={intervalMinutes} one="# minute" other="# minutes" />. Remote progress is never
            lowered and completed entries are never demoted. Configure credentials in Settings.
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
            <Button
              leftSection={<IconRefresh size={16} />}
              loading={syncNow.isPending || data?.running}
              disabled={!anyTrackerConnected}
              onClick={() =>
                syncNow.mutate(undefined, {
                  onSuccess: (r) => notifications.show({ message: r.message }),
                })
              }
            >
              <Trans>Sync now</Trans>
            </Button>
          </Group>
        }
      />

      {error && (
        <Alert color="var(--danger)" variant="light" mb="md">
          {String(error)}
        </Alert>
      )}

      <Title order={4} mb="sm">
        <Trans>Connections</Trans>
      </Title>
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 5 }} mb="lg">
        {data?.connections.map((c) => <ConnectionCard key={c.service} connection={c} />)}
      </SimpleGrid>

      <Group gap="xs" mb="sm">
        <Title order={4}>
          <Trans>Needs review</Trans>
        </Title>
        {data && data.unmatched.length > 0 && (
          <Badge variant="light" color="var(--warn)">
            {data.unmatched.length}
          </Badge>
        )}
      </Group>
      {data && data.unmatched.length > 0 ? (
        <Stack gap="sm" mb="lg">
          {data.unmatched.map((u) => (
            <UnmatchedCard key={`${u.kavitaSeriesId}-${u.service}`} item={u} />
          ))}
        </Stack>
      ) : (
        <Text size="sm" c="var(--ink-3)" mb="lg">
          <Trans>Nothing needs review.</Trans>
        </Text>
      )}

      <Title order={4} mb="sm">
        <Trans>Recent syncs</Trans>
      </Title>
      {data && data.recent.length > 0 ? (
        <Panel p={0} className="table-panel" mb="lg">
          <Table.ScrollContainer minWidth={600}>
            <Table className="panel-table scrobble-recent-table" highlightOnHover>
              <Table.Thead>
                <Table.Tr>
                  <Table.Th>
                    <Trans>Series</Trans>
                  </Table.Th>
                  <Table.Th data-priority="low">
                    <Trans>Service</Trans>
                  </Table.Th>
                  <Table.Th>
                    <Trans>Progress</Trans>
                  </Table.Th>
                  <Table.Th>
                    <Trans>Status</Trans>
                  </Table.Th>
                  <Table.Th data-priority="low">
                    <Trans>When</Trans>
                  </Table.Th>
                </Table.Tr>
              </Table.Thead>
              <Table.Tbody>
                {dedupeKeys(data.recent.map((r) => `${r.at}-${r.service}-${r.title}`)).map((key, i) => {
                  const r = data.recent[i]
                  const { title, service, error, chapter, volume, status, at } = r
                  return (
                    <Table.Tr key={key}>
                      <Table.Td>{title || '#'}</Table.Td>
                      <Table.Td data-priority="low">{serviceLabel(data.connections, service)}</Table.Td>
                      <Table.Td>
                        {error ? (
                          <Tooltip label={error} multiline maw={400}>
                            <Text size="sm" c="var(--danger)" lineClamp={1} className="scrobble-error-text">
                              {error}
                            </Text>
                          </Tooltip>
                        ) : volume ? (
                          <Trans>
                            ch {chapter} · vol {volume}
                          </Trans>
                        ) : (
                          <Trans>ch {chapter}</Trans>
                        )}
                      </Table.Td>
                      <Table.Td>
                        {status ? (
                          (() => {
                            const visual = trackerStatusVisual(status)
                            return (
                              <StatusDot tone={statusToken(visual.color)}>{renderLabel(visual.label)}</StatusDot>
                            )
                          })()
                        ) : (
                          '-'
                        )}
                      </Table.Td>
                      <Table.Td data-priority="low">
                        <Text size="sm" c="var(--ink-3)">
                          {fmtTime(at)}
                        </Text>
                      </Table.Td>
                    </Table.Tr>
                  )
                })}
              </Table.Tbody>
            </Table>
          </Table.ScrollContainer>
        </Panel>
      ) : (
        <Text size="sm" c="var(--ink-3)" mb="lg">
          <Trans>No syncs yet.</Trans>
        </Text>
      )}

      <Title order={4} mb="sm">
        <Trans>Activity log</Trans>
      </Title>
      <Panel p="sm">
        <ScrollArea.Autosize mah={320}>
          {data && data.log.length > 0 ? (
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
            <Text size="sm" c="var(--ink-3)">
              <Trans>Empty.</Trans>
            </Text>
          )}
        </ScrollArea.Autosize>
      </Panel>
    </SurfaceFrame>
  )
}

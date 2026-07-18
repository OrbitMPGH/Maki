import { useEffect, useState } from 'react'
import {
  Alert,
  Badge,
  Box,
  Button,
  Card,
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
import { useSearchParams } from 'react-router-dom'
import { PageHeader } from '../components/ui/PageHeader'
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

function fmtTime(iso: string | null | undefined): string {
  return iso ? new Date(iso).toLocaleString() : '—'
}

function statusColor(status: string | null): string {
  switch (status) {
    case 'completed':
      return 'green'
    case 'reading':
      return 'brand'
    case 'plan_to_read':
      return 'cyan'
    default:
      return 'gray'
  }
}

function ConnectionCard({ connection }: { connection: ScrobbleConnection }) {
  const authStart = useScrobbleAuthStart()
  const disconnect = useScrobbleDisconnect()

  const dotColor = connection.connected ? 'green' : connection.configured ? 'red' : 'gray'
  const state = connection.connected
    ? (connection.username ?? 'connected')
    : connection.configured
      ? 'configured, not connected'
      : 'not configured (see Settings)'

  const connect = () => {
    authStart.mutate(connection.service, {
      onSuccess: (data) => {
        window.location.href = data.url
      },
    })
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Group gap="xs">
        <Box w={10} h={10} bg={dotColor} style={{ borderRadius: '50%' }} />
        <Text fw={700}>{connection.label}</Text>
      </Group>
      <Text size="sm" c="dimmed" mt={4} style={{ wordBreak: 'break-all' }}>
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
                })
              }
            >
              Disconnect
            </Button>
          ) : (
            <Button size="compact-sm" loading={authStart.isPending} onClick={connect}>
              Connect
            </Button>
          )}
        </Group>
      )}
    </Card>
  )
}

function UnmatchedCard({ item }: { item: ScrobbleUnmatchedItem }) {
  const match = useScrobbleMatch()
  const ignore = useScrobbleIgnore()
  const [input, setInput] = useState('')

  const assign = (remoteId: string) => {
    if (!remoteId.trim()) return
    match.mutate(
      { kavitaSeriesId: item.kavitaSeriesId, service: item.service, remoteId: remoteId.trim() },
      {
        onSuccess: (data) => {
          notifications.show({ message: data.message, color: 'green' })
          setInput('')
        },
      },
    )
  }

  return (
    <Card withBorder radius="md" padding="md">
      <Group gap="xs">
        <Text fw={700}>{item.title}</Text>
        <Badge size="sm" variant="light">
          {item.service}
        </Badge>
      </Group>
      <Text size="sm" c="dimmed">
        {item.reason}
      </Text>
      {item.candidates.length > 0 && (
        <Stack gap={4} mt="xs">
          {item.candidates.map((c) => (
            <Group key={c.id} gap="xs">
              <Button size="compact-xs" variant="light" onClick={() => assign(c.id)}>
                Use
              </Button>
              <Text size="sm" component="a" href={c.url} target="_blank" rel="noopener" c="brand.4">
                {c.title}
              </Text>
            </Group>
          ))}
        </Stack>
      )}
      <Group mt="sm" gap="xs" wrap="nowrap">
        <TextInput
          size="xs"
          style={{ flex: 1 }}
          placeholder="Paste series URL or numeric id…"
          value={input}
          onChange={(e) => setInput(e.currentTarget.value)}
          onKeyDown={(e) => e.key === 'Enter' && assign(input)}
        />
        <Button size="compact-sm" onClick={() => assign(input)} loading={match.isPending}>
          Assign
        </Button>
        <Button
          size="compact-sm"
          variant="default"
          loading={ignore.isPending}
          onClick={() =>
            ignore.mutate(
              { kavitaSeriesId: item.kavitaSeriesId, service: item.service },
              {
              },
            )
          }
        >
          Ignore
        </Button>
      </Group>
    </Card>
  )
}

export default function ScrobblePage() {
  const { data, error } = useScrobbleStatus()
  const syncNow = useScrobbleSyncNow()
  const [searchParams, setSearchParams] = useSearchParams()

  // Surface the OAuth redirect result once, then clean the URL.
  useEffect(() => {
    const connected = searchParams.get('connected')
    const oauthError = searchParams.get('error')
    if (connected) {
      notifications.show({ message: `${connected} connected`, color: 'green' })
    }
    if (oauthError) {
      notifications.show({ message: oauthError, color: 'red', autoClose: 10000 })
    }
    if (connected || oauthError) {
      setSearchParams({}, { replace: true })
    }
  }, [searchParams, setSearchParams])

  const anyTrackerConnected = data?.connections.some((c) => c.service !== 'kavita' && c.connected)

  return (
    <>
      <PageHeader
        title="Scrobble"
        description={`Reads reading progress from Kavita and pushes forward-only updates to your trackers every ${data?.intervalMinutes ?? 30} minutes. Remote progress is never lowered and completed entries are never demoted. Configure credentials in Settings.`}
        actions={
          <Group gap="sm">
            <Text size="xs" c="dimmed" ta="right" className="tnum">
              {data?.running ? 'sync running… · ' : ''}
              last {fmtTime(data?.lastSyncAt)} · next {fmtTime(data?.nextSyncAt)}
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
              Sync now
            </Button>
          </Group>
        }
      />

      {error && (
        <Alert color="red" variant="light" mb="md">
          {String(error)}
        </Alert>
      )}

      <Title order={4} mb="sm">
        Connections
      </Title>
      <SimpleGrid cols={{ base: 1, sm: 2, lg: 4 }} mb="lg">
        {data?.connections.map((c) => <ConnectionCard key={c.service} connection={c} />)}
      </SimpleGrid>

      <Group gap="xs" mb="sm">
        <Title order={4}>Needs review</Title>
        {data && data.unmatched.length > 0 && (
          <Badge variant="light" color="yellow">
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
        <Text size="sm" c="dimmed" mb="lg">
          Nothing needs review.
        </Text>
      )}

      <Title order={4} mb="sm">
        Recent syncs
      </Title>
      {data && data.recent.length > 0 ? (
        <Table.ScrollContainer minWidth={600} mb="lg">
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Series</Table.Th>
                <Table.Th>Service</Table.Th>
                <Table.Th>Progress</Table.Th>
                <Table.Th>Status</Table.Th>
                <Table.Th>When</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.recent.map((r, i) => (
                <Table.Tr key={i}>
                  <Table.Td>{r.title || '#'}</Table.Td>
                  <Table.Td>{r.service}</Table.Td>
                  <Table.Td>
                    {r.error ? (
                      <Tooltip label={r.error} multiline maw={400}>
                        <Text size="sm" c="red" lineClamp={1} style={{ maxWidth: 320 }}>
                          {r.error}
                        </Text>
                      </Tooltip>
                    ) : (
                      `ch ${r.chapter}${r.volume ? ` · vol ${r.volume}` : ''}`
                    )}
                  </Table.Td>
                  <Table.Td>
                    {r.status ? (
                      <Badge size="sm" variant="light" color={statusColor(r.status)}>
                        {r.status}
                      </Badge>
                    ) : (
                      '—'
                    )}
                  </Table.Td>
                  <Table.Td>
                    <Text size="sm" c="dimmed">
                      {fmtTime(r.at)}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      ) : (
        <Text size="sm" c="dimmed" mb="lg">
          No syncs yet.
        </Text>
      )}

      <Title order={4} mb="sm">
        Activity log
      </Title>
      <Card withBorder radius="md" padding="sm">
        <ScrollArea.Autosize mah={320}>
          {data && data.log.length > 0 ? (
            <Stack gap={2}>
              {data.log.map((l, i) => (
                // component="div": the line contains a Badge (a div), invalid inside <p>
                <Text key={i} size="xs" ff="monospace" component="div">
                  <Text
                    span
                    c={l.level === 'error' ? 'red' : l.level === 'warning' ? 'yellow' : 'dimmed'}
                  >
                    {fmtTime(l.timestamp)}
                  </Text>{' '}
                  {l.service && (
                    <Badge size="xs" variant="light" mr={4}>
                      {l.service}
                    </Badge>
                  )}
                  {l.title && <Text span fw={600}>{l.title} </Text>}
                  {l.message}
                </Text>
              ))}
            </Stack>
          ) : (
            <Text size="sm" c="dimmed">
              Empty.
            </Text>
          )}
        </ScrollArea.Autosize>
      </Card>
    </>
  )
}

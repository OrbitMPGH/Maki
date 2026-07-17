import {
  Anchor,
  Badge,
  Button,
  Group,
  Stack,
  Table,
  Text,
  TextInput,
  Title,
  Tooltip,
} from '@mantine/core'
import { IconExternalLink, IconRefresh } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { useState } from 'react'
import {
  useScrobbleIgnore,
  useScrobbleMatch,
  useScrobbleSyncNow,
  useSeriesScrobble,
} from '../api/hooks'
import type { SeriesScrobbleServiceDto } from '../api/types'

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

function statusLabel(status: string | null): string {
  switch (status) {
    case 'completed':
      return 'Completed'
    case 'reading':
      return 'Reading'
    case 'plan_to_read':
      return 'Plan to read'
    case 'other':
      return 'Listed'
    default:
      return status ?? '—'
  }
}

/** "-> ch 12" / "ch 12, vol 2" summary for a synced service. */
function progressLabel(s: SeriesScrobbleServiceDto): string {
  const parts: string[] = []
  if (s.chapter > 0) parts.push(`Ch. ${s.chapter}`)
  if (s.volume > 0) parts.push(`Vol. ${s.volume}`)
  return parts.length ? parts.join(' · ') : '—'
}

function ReviewControls({
  kavitaSeriesId,
  service,
}: {
  kavitaSeriesId: number
  service: SeriesScrobbleServiceDto
}) {
  const match = useScrobbleMatch()
  const ignore = useScrobbleIgnore()
  const [manual, setManual] = useState('')

  // Errors are reported globally (see main.tsx); only success needs saying here.
  const notify = {
    ok: (message: string) => notifications.show({ message, color: 'green' }),
  }

  const doMatch = (remoteId: string) =>
    match.mutate(
      { kavitaSeriesId, service: service.service, remoteId },
      { onSuccess: (r) => notify.ok(r.message) },
    )

  return (
    <Stack gap={6}>
      {service.reviewCandidates.slice(0, 3).map((c) => (
        <Group key={c.id} gap={6} wrap="nowrap">
          <Button
            size="compact-xs"
            variant="light"
            loading={match.isPending}
            onClick={() => doMatch(c.id)}
          >
            Use
          </Button>
          <Anchor href={c.url} target="_blank" rel="noopener noreferrer" size="xs" lineClamp={1}>
            {c.title}
          </Anchor>
        </Group>
      ))}
      <Group gap={6} wrap="nowrap">
        <TextInput
          size="xs"
          placeholder="Paste id or URL"
          value={manual}
          onChange={(e) => setManual(e.currentTarget.value)}
          w={160}
        />
        <Button
          size="compact-xs"
          variant="default"
          disabled={!manual.trim()}
          loading={match.isPending}
          onClick={() => doMatch(manual.trim())}
        >
          Link
        </Button>
        <Button
          size="compact-xs"
          variant="subtle"
          color="gray"
          loading={ignore.isPending}
          onClick={() =>
            ignore.mutate(
              { kavitaSeriesId, service: service.service },
              { onSuccess: (r) => notify.ok(r.message) },
            )
          }
        >
          Ignore
        </Button>
      </Group>
    </Stack>
  )
}

export function SeriesScrobbleSection({ seriesId }: { seriesId: number }) {
  const { data } = useSeriesScrobble(seriesId)
  const syncNow = useScrobbleSyncNow()

  // Hide the section entirely when scrobbling isn't set up and there's nothing to show.
  if (!data || (!data.configured && !data.matched)) {
    return null
  }

  return (
    <div>
      <Group justify="space-between" wrap="wrap" gap="sm" mb="sm">
        <Group gap="xs" align="baseline">
          <Title order={3}>Scrobbling</Title>
          {!data.matched && (
            <Text size="sm" c="dimmed">
              not yet synced
            </Text>
          )}
        </Group>
        <Button
          size="xs"
          variant="subtle"
          leftSection={<IconRefresh size={14} />}
          loading={syncNow.isPending}
          onClick={() =>
            syncNow.mutate(undefined, {
              onSuccess: (r) => notifications.show({ message: r.message, color: 'green' }),
            })
          }
        >
          Sync now
        </Button>
      </Group>

      {data.services.length === 0 ? (
        <Text c="dimmed" size="sm">
          No tracker is connected. Connect one on the Scrobble page.
        </Text>
      ) : (
        <Table.ScrollContainer minWidth={560}>
          <Table verticalSpacing="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={120}>Tracker</Table.Th>
                <Table.Th w={150}>Progress</Table.Th>
                <Table.Th>State</Table.Th>
                <Table.Th w={140}>Synced</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.services.map((s) => (
                <Table.Tr key={s.service}>
                  <Table.Td>
                    <Group gap={6} wrap="nowrap">
                      <Tooltip label={s.connected ? 'Connected' : 'Not connected'} withArrow>
                        <span
                          style={{
                            width: 8,
                            height: 8,
                            borderRadius: '50%',
                            background: `var(--mantine-color-${s.connected ? 'green' : 'gray'}-6)`,
                            flexShrink: 0,
                          }}
                        />
                      </Tooltip>
                      <Text size="sm" fw={550}>
                        {s.label}
                      </Text>
                      {s.url && (
                        <Anchor href={s.url} target="_blank" rel="noopener noreferrer" title="Open entry">
                          <IconExternalLink size={13} />
                        </Anchor>
                      )}
                    </Group>
                  </Table.Td>

                  {s.reviewReason ? (
                    <Table.Td colSpan={3}>
                      <Group gap={8} align="flex-start" wrap="nowrap">
                        <Badge size="sm" color="yellow" variant="light">
                          Needs review
                        </Badge>
                        {data.kavitaSeriesId != null && (
                          <ReviewControls kavitaSeriesId={data.kavitaSeriesId} service={s} />
                        )}
                      </Group>
                    </Table.Td>
                  ) : s.method === 'ignored' ? (
                    <Table.Td colSpan={3}>
                      <Badge size="sm" color="gray" variant="light">
                        Ignored
                      </Badge>
                    </Table.Td>
                  ) : (
                    <>
                      <Table.Td>
                        <Text size="sm" className="tnum">
                          {progressLabel(s)}
                        </Text>
                      </Table.Td>
                      <Table.Td>
                        {s.error ? (
                          <Tooltip label={s.error} withArrow multiline w={280}>
                            <Badge size="sm" color="red" variant="light">
                              Error
                            </Badge>
                          </Tooltip>
                        ) : s.syncedAt ? (
                          <Badge size="sm" color={statusColor(s.status)} variant="light">
                            {statusLabel(s.status)}
                          </Badge>
                        ) : (
                          <Text size="sm" c="dimmed">
                            Not yet synced
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm" c="dimmed" className="tnum">
                          {s.syncedAt ? new Date(s.syncedAt).toLocaleDateString() : '—'}
                        </Text>
                      </Table.Td>
                    </>
                  )}
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Table.ScrollContainer>
      )}
    </div>
  )
}

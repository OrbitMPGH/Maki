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
import { formatDate } from '../format'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { useLabel } from '../i18n-context'
import { statusToken, trackerConnectionVisual, trackerStatusVisual } from './ui/status'

/** "-> ch 12" / "ch 12, vol 2" summary for a synced service. */
function progressLabel(s: SeriesScrobbleServiceDto): string {
  const { chapter, volume } = s
  const parts: string[] = []
  if (chapter > 0) parts.push(now`Ch. ${chapter}`)
  if (volume > 0) parts.push(now`Vol. ${volume}`)
  return parts.length ? parts.join(' · ') : '-'
}

function ReviewControls({
  kavitaSeriesId,
  service,
}: {
  kavitaSeriesId: number
  service: SeriesScrobbleServiceDto
}) {
  const { t } = useLingui()
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
            <Trans>Use</Trans>
          </Button>
          <Anchor href={c.url} target="_blank" rel="noopener noreferrer" size="xs" lineClamp={1}>
            {c.title}
          </Anchor>
        </Group>
      ))}
      <Group gap={6}>
        <TextInput
          className="scrobble-review-input"
          size="xs"
          placeholder={t`Paste id or URL`}
          value={manual}
          onChange={(e) => setManual(e.currentTarget.value)}
        />
        <Button
          size="compact-xs"
          variant="default"
          disabled={!manual.trim()}
          loading={match.isPending}
          onClick={() => doMatch(manual.trim())}
        >
          <Trans>Link</Trans>
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
          <Trans>Ignore</Trans>
        </Button>
      </Group>
    </Stack>
  )
}

export function SeriesScrobbleSection({ seriesId }: { seriesId: number }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
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
          <Title order={3}><Trans>Scrobbling</Trans></Title>
          {!data.matched && (
            <Text size="sm" c="var(--ink-3)">
              <Trans>not yet synced</Trans>
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
          <Trans>Sync now</Trans>
        </Button>
      </Group>

      {data.services.length === 0 ? (
        <Text c="var(--ink-3)" size="sm">
          <Trans>No tracker is connected.</Trans> <Trans>Connect one on the Scrobble page.</Trans>
        </Text>
      ) : (
        <Table.ScrollContainer minWidth={560}>
          <Table className="scrobble-table" verticalSpacing="xs">
            <Table.Thead>
              <Table.Tr>
                <Table.Th><Trans>Tracker</Trans></Table.Th>
                <Table.Th><Trans>Progress</Trans></Table.Th>
                <Table.Th><Trans>State</Trans></Table.Th>
                <Table.Th><Trans>Synced</Trans></Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {data.services.map((s) => (
                <Table.Tr key={s.service}>
                  <Table.Td>
                    <Group gap={6} wrap="nowrap">
                      <Tooltip label={s.connected ? t`Connected` : t`Not connected`} withArrow>
                        <span
                          style={{
                            width: 8,
                            height: 8,
                            borderRadius: '50%',
                            background: `var(--${statusToken(trackerConnectionVisual(s.connected, true).color)})`,
                            flexShrink: 0,
                          }}
                        />
                      </Tooltip>
                      <Text size="sm" fw={550}>
                        {s.label}
                      </Text>
                      {s.url && (
                        <Anchor href={s.url} target="_blank" rel="noopener noreferrer" title={t`Open entry`}>
                          <IconExternalLink size={13} />
                        </Anchor>
                      )}
                    </Group>
                  </Table.Td>

                  {s.reviewReason ? (
                    <Table.Td colSpan={3}>
                      <Group gap={8} align="flex-start" wrap="nowrap">
                        <Badge size="sm" color="yellow" variant="light">
                          <Trans>Needs review</Trans>
                        </Badge>
                        {data.kavitaSeriesId != null && (
                          <ReviewControls kavitaSeriesId={data.kavitaSeriesId} service={s} />
                        )}
                      </Group>
                    </Table.Td>
                  ) : s.method === 'ignored' ? (
                    <Table.Td colSpan={3}>
                      <Badge size="sm" color="gray" variant="light">
                        <Trans>Ignored</Trans>
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
                              <Trans>Error</Trans>
                            </Badge>
                          </Tooltip>
                        ) : s.syncedAt ? (
                          (() => {
                            const visual = trackerStatusVisual(s.status ?? '')
                            return (
                              <Badge size="sm" color={`var(--${statusToken(visual.color)})`} variant="light">
                                {renderLabel(visual.label)}
                              </Badge>
                            )
                          })()
                        ) : (
                          <Text size="sm" c="var(--ink-3)">
                            <Trans>Not yet synced</Trans>
                          </Text>
                        )}
                      </Table.Td>
                      <Table.Td>
                        <Text size="sm" c="var(--ink-3)" className="tnum">
                          {s.syncedAt ? formatDate(s.syncedAt) : '-'}
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

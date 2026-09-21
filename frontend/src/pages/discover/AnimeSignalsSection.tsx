import { useMemo, useState } from 'react'
import {
  Alert, Badge, Button, Chip, Group, Paper, Stack, Switch, Text,
} from '@mantine/core'
import { Link } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { AnimeSignalEntry, AnimeSignalRole } from '../../api/animeSignals'
import { useAnimeSignals, useSetAnimeSignalsEnabled, useSyncAnimeSignals } from '../../api/animeSignals'
import { formatDateTime } from '../../format'

type RoleFilter = AnimeSignalRole | 'all'

/**
 * Watched anime, from a connected AniList or MyAnimeList tracker, matched to catalogue manga and
 * used as extra recommendation seeds. Shown only when the capability is on; the section itself
 * still has to tell "no tracker connected" apart from "instance switch off", since both come back
 * as `capabilities.animeSignals === false` upstream but need different copy here.
 */
export function AnimeSignalsSection() {
  const { t } = useLingui()
  const { data, isLoading, error } = useAnimeSignals()
  const setEnabled = useSetAnimeSignalsEnabled()
  const sync = useSyncAnimeSignals()
  const [toggleError, setToggleError] = useState('')
  const [syncError, setSyncError] = useState('')
  const [roleFilter, setRoleFilter] = useState<RoleFilter>('all')

  const counts = data?.counts
  const unmatched = counts ? counts.total - counts.matched : 0

  const filtered = useMemo(() => {
    const entries = data?.entries ?? []
    return roleFilter === 'all' ? entries : entries.filter((entry) => entry.role === roleFilter)
  }, [data, roleFilter])
  const entries = data?.entries ?? []

  async function toggle(next: boolean) {
    setToggleError('')
    try { await setEnabled.mutateAsync(next) } catch (cause) { setToggleError(String(cause)) }
  }

  async function runSync() {
    setSyncError('')
    try { await sync.mutateAsync() } catch (cause) { setSyncError(String(cause)) }
  }

  if (isLoading || !data) {
    return error ? (
      <Alert color="red"><Trans>Could not load anime signals: {String(error)}</Trans></Alert>
    ) : null
  }

  if (!data.instanceEnabled) return null

  const noTracker = data.services.length === 0

  const roles: { value: RoleFilter; label: string; count: number }[] = [
    { value: 'all', label: t`All`, count: counts?.total ?? 0 },
    { value: 'positive', label: t`Positive`, count: counts?.positive ?? 0 },
    { value: 'avoided', label: t`Avoided`, count: counts?.avoided ?? 0 },
    { value: 'neutral', label: t`Neutral`, count: (counts?.matched ?? 0) - (counts?.positive ?? 0) - (counts?.avoided ?? 0) },
    { value: 'unmatched', label: t`Unmatched`, count: counts?.ignored ?? unmatched },
  ]

  return (
    <Paper withBorder radius="md" p="md">
      <Stack gap="sm">
        <Group justify="space-between" align="flex-start" wrap="wrap">
          <div>
            <Text fw={600}><Trans>Anime you've watched</Trans></Text>
            <Text size="xs" c="dimmed" maw={520}>
              <Trans>
                A watched anime with a good score gently steers recommendations toward similar
                manga. A low score or a dropped show pushes down titles close to it, the same way a
                thumbs down does. A show listed on both your trackers counts once, and a
                franchise's seasons are averaged into one score.
              </Trans>
            </Text>
          </div>
          <Switch
            checked={data.enabled} onChange={(event) => void toggle(event.currentTarget.checked)}
            disabled={setEnabled.isPending || noTracker}
            label={data.enabled ? t`On` : t`Off`}
          />
        </Group>

        {toggleError && <Alert color="red">{toggleError}</Alert>}

        {noTracker && (
          <Text size="sm" c="dimmed">
            <Trans>
              Connect an AniList or MyAnimeList tracker to use this. <Link to="/scrobble">
                <Trans>Open tracker settings</Trans>
              </Link>
            </Trans>
          </Text>
        )}

        {data.enabled && !noTracker && (
          <>
            <Group justify="space-between" align="center" wrap="wrap">
              <Text size="xs" c="dimmed">
                {data.lastSyncAtUtc
                  ? t`Last synced ${formatDateTime(data.lastSyncAtUtc)}`
                  : t`Not synced yet`}
                {counts && (
                  <>
                    {' · '}
                    <Trans>
                      {counts.matched} of {counts.total} matched, {counts.positive} positive,{' '}
                      {counts.avoided} avoided
                    </Trans>
                  </>
                )}
              </Text>
              <Button size="xs" variant="outline" loading={data.syncing} onClick={() => void runSync()}>
                <Trans>Sync now</Trans>
              </Button>
            </Group>

            {syncError && <Alert color="red">{syncError}</Alert>}

            {entries.length > 0 && (
              <>
                <Chip.Group multiple={false} value={roleFilter} onChange={(value) => setRoleFilter(value as RoleFilter)}>
                  <Group gap={6} wrap="wrap">
                    {roles.map((role) => (
                      <Chip key={role.value} value={role.value} size="xs" variant="outline">
                        {role.label} · {role.count}
                      </Chip>
                    ))}
                  </Group>
                </Chip.Group>

                <Stack gap={4}>
                  {filtered.slice(0, 40).map((entry) => (
                    <AnimeSignalRow key={entry.key} entry={entry} />
                  ))}
                </Stack>
              </>
            )}
          </>
        )}
      </Stack>
    </Paper>
  )
}

function AnimeSignalRow({ entry }: { entry: AnimeSignalEntry }) {
  const { t } = useLingui()
  const labelFor = (service: string) =>
    service === 'anilist' ? t`AniList` : service === 'mal' ? t`MyAnimeList` : service

  const statusLabel = ((): string => {
    switch (entry.status) {
      case 'Watching': return t`Watching`
      case 'Completed': return t`Completed`
      case 'OnHold': return t`On hold`
      case 'Dropped': return t`Dropped`
      case 'Planning': return t`Planning`
      default: return entry.status
    }
  })()

  const roleBadge = ((): { label: string; color: string } => {
    switch (entry.role) {
      case 'positive': return { label: t`Positive`, color: 'teal' }
      case 'avoided': return { label: t`Avoided`, color: 'red' }
      case 'neutral': return { label: t`Neutral`, color: 'gray' }
      default: return { label: t`Unmatched`, color: 'gray' }
    }
  })()

  // One decimal only when averaging produced one: a show watched once still reads "★ 8".
  const score = entry.score === null
    ? null
    : Number.isInteger(entry.score) ? String(entry.score) : entry.score.toFixed(1)

  return (
    <Group gap="sm" wrap="nowrap" py={4}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <Group gap={6} wrap="nowrap">
          <Text size="sm" fw={500} truncate>{entry.title}</Text>
          {entry.animeCount > 1 && (
            <Badge size="xs" variant="default" style={{ flexShrink: 0 }}>
              <Plural value={entry.animeCount} one="# season" other="# seasons" />
            </Badge>
          )}
        </Group>
        <Text size="xs" c="dimmed" truncate>
          {entry.mangaTitle ?? t`No manga match`}
        </Text>
      </div>
      {entry.services.map((service) => (
        <Badge key={service} size="sm" variant="light" color="gray" style={{ flexShrink: 0 }}>
          {labelFor(service)}
        </Badge>
      ))}
      <Text size="xs" c="dimmed" style={{ flexShrink: 0, width: 70, textAlign: 'right' }}>
        {score !== null ? t`★ ${score}` : statusLabel}
      </Text>
      <Badge size="sm" variant="light" color={roleBadge.color} style={{ flexShrink: 0 }}>
        {roleBadge.label}
      </Badge>
    </Group>
  )
}

import { useMemo, useState } from 'react'
import {
  Alert, Badge, Button, Chip, Collapse, Group, Paper, SegmentedControl, Stack, Switch, Text, Tooltip,
} from '@mantine/core'
import { Link } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { AnimeSignalEntry, AnimeSignalRole, AnimeSignalStrength } from '../../api/animeSignals'
import {
  useAnimeSignals, useSetAnimeSignalsEnabled, useSetAnimeSignalsStrength, useSyncAnimeSignals,
} from '../../api/animeSignals'
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
  const setStrength = useSetAnimeSignalsStrength()
  const sync = useSyncAnimeSignals()
  const [toggleError, setToggleError] = useState('')
  const [syncError, setSyncError] = useState('')
  const [roleFilter, setRoleFilter] = useState<RoleFilter>('all')
  const [listOpen, setListOpen] = useState(false)

  const counts = data?.counts

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

  async function changeStrength(strength: AnimeSignalStrength) {
    setToggleError('')
    try {
      await setStrength.mutateAsync({ enabled: true, strength })
    } catch (cause) {
      setToggleError(String(cause))
    }
  }

  if (isLoading || !data) {
    return error ? (
      <Alert color="red"><Trans>Could not load anime signals: {String(error)}</Trans></Alert>
    ) : null
  }

  if (!data.instanceEnabled) return null

  const noTracker = data.services.length === 0

  // Straight off the payload. These used to be derived here by subtracting the roles from the
  // matched count, which made the unmatched chip report every neutral entry as well.
  const roles: { value: RoleFilter; label: string; count: number }[] = [
    { value: 'all', label: t`All`, count: counts?.total ?? 0 },
    { value: 'positive', label: t`Positive`, count: counts?.positive ?? 0 },
    { value: 'avoided', label: t`Avoided`, count: counts?.avoided ?? 0 },
    { value: 'neutral', label: t`Neutral`, count: counts?.neutral ?? 0 },
    { value: 'superseded', label: t`Already yours`, count: counts?.superseded ?? 0 },
    { value: 'unmatched', label: t`Unmatched`, count: counts?.unmatched ?? 0 },
  ]

  return (
    <Paper withBorder radius="md" p="md">
      <Stack gap="sm">
        <Group justify="space-between" align="center" wrap="wrap">
          <div>
            <Text fw={600}><Trans>Anime you've watched</Trans></Text>
            <Text size="xs" c="dimmed" maw={520}>
              <Trans>
                A show you scored well nudges recommendations toward similar manga. A low score or
                a dropped show pushes them away.
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
              <AnimeSignalStrengthControl
                value={data.strength}
                topSeedWeight={data.strengths.find((s) => s.value === data.strength)?.topSeedWeight}
                pending={setStrength.isPending}
                onChange={(next) => void changeStrength(next)}
              />
              <Button size="xs" variant="outline" loading={data.syncing} onClick={() => void runSync()}>
                <Trans>Sync now</Trans>
              </Button>
            </Group>

            {syncError && <Alert color="red">{syncError}</Alert>}

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
                      {counts.avoided} avoided, {counts.superseded} already yours
                    </Trans>
                  </>
                )}
              </Text>
              {entries.length > 0 && (
                <Button size="xs" variant="subtle" onClick={() => setListOpen((v) => !v)}>
                  {listOpen
                    ? <Trans>Hide list</Trans>
                    : <Plural value={entries.length} one="Show # show" other="Show # shows" />}
                </Button>
              )}
            </Group>

            {entries.length > 0 && (
              <Collapse expanded={listOpen}>
                <Stack gap="sm">
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
                </Stack>
              </Collapse>
            )}
          </>
        )}
      </Stack>
    </Paper>
  )
}

/**
 * How much authority a watched anime carries, as the three-step control the grids use for density.
 *
 * Three named steps rather than a continuous slider because the number underneath is not something
 * anybody can aim: the levels are 0.35, 0.5 and 1.0 of what rating the manga yourself would carry,
 * and a reader dragging to 0.62 would be expressing precision the signal does not have. The hint
 * line names the one number that is legible - what a 10/10 anime ends up seeding at, against the
 * 1.0 an unrated book on the shelf already gets.
 */
function AnimeSignalStrengthControl({
  value,
  topSeedWeight,
  pending,
  onChange,
}: {
  value: AnimeSignalStrength
  topSeedWeight: number | undefined
  pending: boolean
  onChange: (value: AnimeSignalStrength) => void
}) {
  const { t } = useLingui()

  const hint = ((): string => {
    switch (value) {
      case 'subtle':
        return t`Barely nudges. A show you loved stays below a book you never rated.`
      case 'full':
        return t`A watched anime counts for as much as a manga you rated yourself.`
      default:
        return t`Half of what rating the manga would carry. A show you loved lends its genres and tags to your profile without out-voting a book you actually read.`
    }
  })()

  const tooltipLabel = (
    <>
      {hint}
      {topSeedWeight !== undefined && (
        <>
          {' '}
          <Trans>A 10/10 anime seeds at {topSeedWeight.toFixed(2)}, against 1.00 for an unrated book on your shelf.</Trans>
        </>
      )}
    </>
  )

  return (
    <Group gap="sm" wrap="wrap" align="center">
      <Text size="xs" fw={500}><Trans>How much they count</Trans></Text>
      <Tooltip label={tooltipLabel} multiline w={300}>
        <SegmentedControl
          size="xs"
          value={value}
          disabled={pending}
          onChange={(next) => onChange(next as AnimeSignalStrength)}
          data={[
            { value: 'subtle', label: t`Subtle` },
            { value: 'balanced', label: t`Balanced` },
            { value: 'full', label: t`Full` },
          ]}
        />
      </Tooltip>
    </Group>
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
      case 'superseded': return { label: t`Already yours`, color: 'gray' }
      default: return { label: t`Unmatched`, color: 'gray' }
    }
  })()

  // Why this row counts for nothing, said plainly. Without it a manga the reader rated themselves
  // sits in the list looking exactly like one that is steering recommendations.
  const supersededNote = ((): string | null => {
    switch (entry.supersededBy) {
      case 'library': return t`Already on your shelf, so your own reading counts instead`
      case 'feedback': return t`You rated this one yourself, so the anime is not counted again`
      case 'ignored': return t`You excluded this title from recommendations`
      default: return null
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
          {supersededNote ?? entry.mangaTitle ?? t`No manga match`}
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

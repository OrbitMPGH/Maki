import { useMemo, useState } from 'react'
import {
  Alert, Badge, Button, Card, Group, SegmentedControl, Stack, Switch, Text, Tooltip,
} from '@mantine/core'
import { Link } from 'react-router-dom'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type {
  AnimeSignalCounts, AnimeSignalEntry, AnimeSignalRole, AnimeSignalsData, AnimeSignalStrength,
} from '../../api/animeSignals'
import { useAnimeSignals, useSetAnimeSignalsEnabled, useSetAnimeSignalsStrength } from '../../api/animeSignals'
import { formatDateTime } from '../../format'
import { SeriesThumb } from '../stats/SeriesLink'

export type RoleFilter = AnimeSignalRole | 'all'

/**
 * The last-synced line, shared by the Taste tab's summary and the Manage signals modal's Anime
 * tab: a running pass says how far it's gotten, otherwise it says when it last finished.
 */
export function AnimeSignalsStatusLine({ data }: { data: AnimeSignalsData }) {
  const { t } = useLingui()

  if (data.syncing) {
    if (data.progress) {
      const looked = data.progress.looked
      const total = data.progress.total
      return <Trans>Looking up {looked} of {total}</Trans>
    }
    return <Trans>Syncing…</Trans>
  }

  const synced = data.lastSyncAtUtc ? formatDateTime(data.lastSyncAtUtc) : ''

  return (
    <>
      {data.lastSyncAtUtc ? t`Last synced ${synced}` : t`Not synced yet`}
      {data.counts && (() => {
        const matched = data.counts.matched
        const total = data.counts.total
        const positive = data.counts.positive
        const avoided = data.counts.avoided
        const superseded = data.counts.superseded
        return (
          <>
            {' · '}
            <Trans>
              {matched} of {total} matched, {positive} positive,{' '}
              {avoided} avoided, {superseded} already yours
            </Trans>
          </>
        )
      })()}
    </>
  )
}

/**
 * The roles a reader can filter the anime signal list by, with their counts, so the modal does not
 * duplicate the labels this section already defines.
 */
export function useAnimeRoleFilters(counts: AnimeSignalCounts | undefined) {
  const { t } = useLingui()
  const roles: { value: RoleFilter; label: string; count: number }[] = [
    { value: 'all', label: t`All`, count: counts?.total ?? 0 },
    { value: 'positive', label: t`Positive`, count: counts?.positive ?? 0 },
    { value: 'avoided', label: t`Avoided`, count: counts?.avoided ?? 0 },
    { value: 'neutral', label: t`Neutral`, count: counts?.neutral ?? 0 },
    { value: 'superseded', label: t`Already yours`, count: counts?.superseded ?? 0 },
    { value: 'unmatched', label: t`Unmatched`, count: counts?.unmatched ?? 0 },
  ]
  return roles
}

/**
 * Watched anime, from a connected AniList or MyAnimeList tracker, matched to catalogue manga and
 * used as extra recommendation seeds. Shown only when the capability is on; the section itself
 * still has to tell "no tracker connected" apart from "instance switch off", since both come back
 * as `capabilities.animeSignals === false` upstream but need different copy here. The full list
 * lives in the Manage signals modal's Anime tab; this panel only sets the dial.
 */
export function AnimeSignalsSection({ onOpenList }: { onOpenList: () => void }) {
  const { t } = useLingui()
  const { data, isLoading, error } = useAnimeSignals()
  const setEnabled = useSetAnimeSignalsEnabled()
  const setStrength = useSetAnimeSignalsStrength()
  const [toggleError, setToggleError] = useState('')

  async function toggle(next: boolean) {
    setToggleError('')
    try { await setEnabled.mutateAsync(next) } catch (cause) { setToggleError(String(cause)) }
  }

  async function changeStrength(strength: AnimeSignalStrength) {
    setToggleError('')
    try {
      await setStrength.mutateAsync({ enabled: true, strength })
    } catch (cause) {
      setToggleError(String(cause))
    }
  }

  // A taste of what the list holds so the card is not two buttons on an empty band: the shows
  // that actually steer the ranking first, then whatever else is scored highest.
  const preview = useMemo(() => {
    const entries = data?.entries ?? []
    const weight = (role: AnimeSignalEntry['role']) => (role === 'positive' || role === 'avoided' ? 0 : 1)
    return [...entries]
      .sort((a, b) => weight(a.role) - weight(b.role) || (b.score ?? 0) - (a.score ?? 0))
      .slice(0, 5)
  }, [data])
  const total = data?.entries.length ?? 0

  if (isLoading || !data) {
    return error ? (
      <Alert color="red"><Trans>Could not load anime signals: {String(error)}</Trans></Alert>
    ) : null
  }

  if (!data.instanceEnabled) return null

  const noTracker = data.services.length === 0

  return (
    <Card withBorder radius="lg" padding="md">
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
          <Group justify="space-between" align="center" wrap="wrap">
            <AnimeSignalStrengthControl
              value={data.strength}
              topSeedWeight={data.strengths.find((s) => s.value === data.strength)?.topSeedWeight}
              pending={setStrength.isPending}
              onChange={(next) => void changeStrength(next)}
            />
            <Group gap="xs">
              <Text size="xs" c="dimmed">
                <AnimeSignalsStatusLine data={data} />
              </Text>
              <Button size="xs" variant="subtle" onClick={onOpenList}>
                <Plural value={total} one="Show all #" other="Show all #" />
              </Button>
            </Group>
          </Group>
        )}

        {data.enabled && !noTracker && preview.length > 0 && (
          <Stack gap={0}>
            {preview.map((entry) => (
              <AnimeSignalRow key={entry.key} entry={entry} />
            ))}
          </Stack>
        )}
      </Stack>
    </Card>
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

  const topSeed = topSeedWeight !== undefined ? topSeedWeight.toFixed(2) : ''

  const tooltipLabel = (
    <>
      {hint}
      {topSeedWeight !== undefined && (
        <>
          {' '}
          <Trans>A 10/10 anime seeds at {topSeed}, against 1.00 for an unrated book on your shelf.</Trans>
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

export function AnimeSignalRow({ entry }: { entry: AnimeSignalEntry }) {
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
    <Group gap="sm" wrap="nowrap" align="flex-start" py={8} className="signals-row">
      <SeriesThumb url={entry.mangaCoverUrl} alt={entry.mangaTitle ?? ''} large />
      <div style={{ flex: 1, minWidth: 0 }}>
        <Group gap={6} wrap="nowrap" style={{ minWidth: 0 }}>
          <Text size="sm" fw={500} truncate style={{ minWidth: 0 }}>{entry.title}</Text>
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
      <div style={{ width: 120, flex: 'none' }}>
        <Group gap={4} wrap="wrap">
          {entry.services.map((service) => (
            <Badge key={service} size="sm" variant="light" color="gray">
              {labelFor(service)}
            </Badge>
          ))}
        </Group>
      </div>
      <Text size="sm" className="tnum" style={{ width: 80, flex: 'none' }}>
        {score !== null ? t`★ ${score}` : statusLabel}
      </Text>
      <div style={{ width: 110, flex: 'none' }}>
        <Badge size="sm" variant="light" color={roleBadge.color}>
          {roleBadge.label}
        </Badge>
      </div>
    </Group>
  )
}

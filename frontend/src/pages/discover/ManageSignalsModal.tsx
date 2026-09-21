import { useEffect, useMemo, useState } from 'react'
import {
  ActionIcon, Alert, Badge, Button, Chip, Group, Modal, ScrollArea, Select, Stack, Tabs, Text,
  TextInput,
} from '@mantine/core'
import { IconSearch, IconX } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Trans, useLingui } from '@lingui/react/macro'
import type { FeedbackState } from '../../api/recommendationFeedback'
import {
  useFeedbackLab, useFeedbackStates, useMutateFeedback, useMutateSignalOverride, useSignalOverrides,
} from '../../api/recommendationFeedback'
import { useAnimeSignals, useSyncAnimeSignals } from '../../api/animeSignals'
import { AnimeSignalRow, useAnimeRoleFilters } from './AnimeSignalsSection'
import type { RoleFilter } from './AnimeSignalsSection'
import { SeriesThumb } from '../stats/SeriesLink'
import { formatDate, formatDateTime } from '../../format'

type Filter = 'all' | 'rated' | 'thumbs' | 'hidden' | 'exposed' | 'excluded' | 'added'

interface Row {
  id: number
  title: string
  genres: string[]
  coverUrl: string | null
  rating: number | null
  isRead: boolean
  onShelf: boolean
  addedByYou: boolean
  excluded: boolean
  state: FeedbackState | null
  changedAt: number
}

/**
 * Every title that steers the ranking, and the one control each of them needs.
 *
 * The shelf and the feedback states are separate stores, so a title can appear in either or both;
 * they are merged on the catalogue id here rather than joined on the server, which would mean
 * paging two collections against one cursor. An Anime tab sits beside it when the capability is on,
 * holding the full anime-signal list the Taste tab's panel only summarizes.
 */
export function ManageSignalsModal({ opened, onClose, initialTab = 'titles' }: {
  opened: boolean
  onClose: () => void
  initialTab?: 'titles' | 'anime'
}) {
  const { t } = useLingui()
  const [tab, setTab] = useState<'titles' | 'anime'>(initialTab)
  const [cursor, setCursor] = useState<number | undefined>()
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<Filter>('all')
  const [sort, setSort] = useState<'recent' | 'title'>('recent')
  const [actionError, setActionError] = useState('')
  const [pending, setPending] = useState<number | null>(null)

  const { data: lab } = useFeedbackLab()
  useEffect(() => {
    if (opened) setTab(initialTab)
  }, [opened, initialTab])
  const { data: states } = useFeedbackStates(cursor, 'recent')
  const { data: overrides } = useSignalOverrides()
  const feedback = useMutateFeedback()
  const signal = useMutateSignalOverride()

  // Accumulated feedback pages, tagged with the revision they were read at. A page from a newer
  // revision replaces the pile rather than merging into it, so rows the server dropped go with
  // it. Keyed on the page itself, never on `opened`: clearing on open left the pile empty whenever
  // the first page was already cached, since a cached page never re-fires the merge.
  const [loaded, setLoaded] = useState<{ revision: string; items: FeedbackState[] }>({ revision: '', items: [] })
  const revision = `${states?.feedbackRevision ?? 0}:${states?.signalRevision ?? 0}`
  useEffect(() => {
    if (!states) return
    setLoaded((prev) => {
      const base = prev.revision === revision ? prev.items : []
      const merged = new Map(base.map((item) => [item.mangaBakaId, item]))
      for (const item of states.items) merged.set(item.mangaBakaId, item)
      return { revision, items: [...merged.values()] }
    })
  }, [states, revision])
  useEffect(() => { setCursor(undefined) }, [revision])

  const rows = useMemo<Row[]>(() => {
    const byId = new Map<number, Row>()
    for (const source of lab?.sources ?? []) {
      byId.set(source.mangaBakaId, {
        id: source.mangaBakaId,
        title: source.title,
        genres: source.genres ?? [],
        coverUrl: source.coverUrl,
        rating: source.rating,
        isRead: source.isRead,
        onShelf: true,
        addedByYou: source.addedAtUtc !== null,
        excluded: source.excluded,
        state: null,
        changedAt: 0,
      })
    }
    for (const state of loaded.items) {
      const existing = byId.get(state.mangaBakaId)
      byId.set(state.mangaBakaId, {
        id: state.mangaBakaId,
        title: state.title ?? existing?.title ?? '',
        genres: existing?.genres.length ? existing.genres : state.genres ?? [],
        coverUrl: existing?.coverUrl ?? state.coverUrl,
        rating: existing?.rating ?? null,
        isRead: existing?.isRead ?? false,
        onShelf: existing?.onShelf ?? false,
        addedByYou: existing?.addedByYou ?? false,
        excluded: existing?.excluded ?? false,
        state,
        changedAt: state.updatedAtUtc ? new Date(state.updatedAtUtc).getTime() : 0,
      })
    }
    const all = [...byId.values()]
    return sort === 'title'
      ? all.sort((a, b) => a.title.localeCompare(b.title))
      : all.sort((a, b) => b.changedAt - a.changedAt || a.title.localeCompare(b.title))
  }, [lab, loaded.items, sort])

  const counts = useMemo(() => ({
    all: rows.length,
    rated: rows.filter((row) => row.rating !== null).length,
    thumbs: rows.filter((row) => row.state && row.state.sentiment !== 'none').length,
    hidden: rows.filter((row) => row.state && row.state.suppression !== 'none').length,
    exposed: rows.filter((row) => (row.state?.exposure.length ?? 0) > 0).length,
    excluded: rows.filter((row) => row.excluded).length,
    added: rows.filter((row) => row.addedByYou).length,
  }), [rows])

  const visible = useMemo(() => {
    const needle = search.trim().toLowerCase()
    return rows.filter((row) => {
      if (needle && !row.title.toLowerCase().includes(needle)) return false
      switch (filter) {
        case 'rated': return row.rating !== null
        case 'thumbs': return !!row.state && row.state.sentiment !== 'none'
        case 'hidden': return !!row.state && row.state.suppression !== 'none'
        case 'exposed': return (row.state?.exposure.length ?? 0) > 0
        case 'excluded': return row.excluded
        case 'added': return row.addedByYou
        default: return true
      }
    })
  }, [rows, search, filter])

  async function run(id: number, work: () => Promise<unknown>) {
    setActionError('')
    setPending(id)
    try { await work() } catch (cause) { setActionError(String(cause)) } finally { setPending(null) }
  }

  const clear = (row: Row, action: 'clear-suppression' | 'clear-exposure' | 'clear-sentiment') =>
    run(row.id, () => feedback.mutateAsync({
      id: row.id, action, expectedRevision: row.state?.revision ?? 0,
      clientMutationId: crypto.randomUUID(),
    }))

  const setExcluded = (row: Row, ignoreAsSeed: boolean) =>
    run(row.id, () => signal.mutateAsync({
      id: row.id, ignoreAsSeed, clientMutationId: crypto.randomUUID(),
      expectedRevision: overrides?.find((item) => item.mangaBakaId === row.id)?.revision ?? 0,
    }))

  const filters: { value: Filter; label: string; count: number }[] = [
    { value: 'all', label: t`All`, count: counts.all },
    { value: 'rated', label: t`Rated`, count: counts.rated },
    { value: 'thumbs', label: t`Thumbs`, count: counts.thumbs },
    { value: 'hidden', label: t`Hidden`, count: counts.hidden },
    { value: 'exposed', label: t`Seen elsewhere`, count: counts.exposed },
    { value: 'excluded', label: t`Excluded from taste`, count: counts.excluded },
    { value: 'added', label: t`Added by you`, count: counts.added },
  ]

  const shown = visible.length
  const total = rows.length

  return (
    <Modal
      opened={opened} onClose={onClose} size="xl" radius="lg"
      closeButtonProps={{ 'aria-label': t`Close` }}
      title={
        <div>
          <Text fw={600}><Trans>Manage signals</Trans></Text>
          <Text size="sm" c="dimmed">
            <Trans>Change how individual titles affect recommendations.</Trans>
          </Text>
        </div>
      }
    >
      <Tabs value={tab} onChange={(value) => setTab(value === 'anime' ? 'anime' : 'titles')}>
        <Tabs.List mb="sm">
          <Tabs.Tab value="titles"><Trans>Titles</Trans></Tabs.Tab>
          {lab?.capabilities.animeSignals && (
            <Tabs.Tab value="anime"><Trans>Anime</Trans></Tabs.Tab>
          )}
        </Tabs.List>

        <Tabs.Panel value="titles">
          <Stack gap="sm">
            <Group gap="sm" align="center" wrap="wrap">
              <TextInput
                value={search} onChange={(event) => setSearch(event.currentTarget.value)}
                placeholder={t`Search titles`} leftSection={<IconSearch size={16} />}
                w={240} aria-label={t`Search titles`}
              />
              <Select
                value={sort} onChange={(value) => setSort(value === 'title' ? 'title' : 'recent')}
                aria-label={t`Sort`} w={190} allowDeselect={false}
                data={[
                  { value: 'recent', label: t`Recently changed` },
                  { value: 'title', label: t`Title` },
                ]}
              />
            </Group>
            <Chip.Group multiple={false} value={filter} onChange={(value) => setFilter(value as Filter)}>
              <Group gap={6} wrap="wrap">
                {filters.map((item) => (
                  <Chip key={item.value} value={item.value} size="xs" variant="outline">
                    {item.label} · {item.count}
                  </Chip>
                ))}
              </Group>
            </Chip.Group>

            {actionError && (
              <Alert color="red">
                <Trans>{actionError} Refresh the page and try again.</Trans>
              </Alert>
            )}

            {visible.length === 0 && (
              <Text size="sm" c="dimmed" py="md"><Trans>No titles match.</Trans></Text>
            )}

            <Stack gap={0}>
              {visible.map((row) => (
                <SignalRow
                  key={row.id} row={row} busy={pending === row.id}
                  onClear={clear} onExclude={setExcluded}
                />
              ))}
            </Stack>

            <Group justify="space-between">
              <Text size="xs" c="dimmed"><Trans>Showing {shown} of {total}</Trans></Text>
              {states?.nextCursor && (
                <Button size="xs" variant="subtle" onClick={() => setCursor(states.nextCursor!)}>
                  <Trans>Load more</Trans>
                </Button>
              )}
            </Group>
          </Stack>
        </Tabs.Panel>

        {lab?.capabilities.animeSignals && (
          <Tabs.Panel value="anime">
            <AnimeSignalsPanel />
          </Tabs.Panel>
        )}
      </Tabs>
    </Modal>
  )
}

/**
 * The full anime-signal list: search, role filter and every matching entry, unbounded. The Taste
 * tab's panel only shows the dial and a summary; this is where a reader checks one specific title.
 */
function AnimeSignalsPanel() {
  const { t } = useLingui()
  const { data, isLoading, error } = useAnimeSignals()
  const sync = useSyncAnimeSignals()
  const [syncError, setSyncError] = useState('')
  const [search, setSearch] = useState('')
  const [roleFilter, setRoleFilter] = useState<RoleFilter>('all')

  const roles = useAnimeRoleFilters(data?.counts)

  const filtered = useMemo(() => {
    const needle = search.trim().toLowerCase()
    return (data?.entries ?? []).filter((entry) => {
      if (roleFilter !== 'all' && entry.role !== roleFilter) return false
      if (!needle) return true
      return entry.title.toLowerCase().includes(needle) ||
        (entry.mangaTitle?.toLowerCase().includes(needle) ?? false)
    })
  }, [data?.entries, search, roleFilter])
  const shown = filtered.length
  const total = data?.entries.length ?? 0

  async function runSync() {
    setSyncError('')
    try {
      await sync.mutateAsync()
      notifications.show({ message: t`Synced.`, color: 'green' })
    } catch (cause) {
      setSyncError(String(cause))
    }
  }

  if (isLoading || !data) {
    return error ? (
      <Alert color="red"><Trans>Could not load anime signals: {String(error)}</Trans></Alert>
    ) : null
  }

  const noTracker = data.services.length === 0
  if (!data.enabled || noTracker) {
    return (
      <Text size="sm" c="dimmed" py="md">
        {noTracker
          ? <Trans>Connect an AniList or MyAnimeList tracker to use this.</Trans>
          : <Trans>Turn on anime signals on the Taste tab to use this.</Trans>}
      </Text>
    )
  }

  return (
    <Stack gap="sm">
      <Group justify="space-between" align="center" wrap="wrap">
        <Text size="xs" c="dimmed">
          {data.lastSyncAtUtc
            ? t`Last synced ${formatDateTime(data.lastSyncAtUtc)}`
            : t`Not synced yet`}
          {data.counts && (
            <>
              {' · '}
              <Trans>
                {data.counts.matched} of {data.counts.total} matched, {data.counts.positive} positive,{' '}
                {data.counts.avoided} avoided, {data.counts.superseded} already yours
              </Trans>
            </>
          )}
        </Text>
        <Button size="xs" variant="outline" loading={sync.isPending || data.syncing} onClick={() => void runSync()}>
          <Trans>Sync now</Trans>
        </Button>
      </Group>

      {syncError && <Alert color="red">{syncError}</Alert>}

      <TextInput
        value={search} onChange={(event) => setSearch(event.currentTarget.value)}
        placeholder={t`Search shows`} leftSection={<IconSearch size={16} />}
        aria-label={t`Search shows`}
      />

      <Chip.Group multiple={false} value={roleFilter} onChange={(value) => setRoleFilter(value as RoleFilter)}>
        <Group gap={6} wrap="wrap">
          {roles.map((role) => (
            <Chip key={role.value} value={role.value} size="xs" variant="outline">
              {role.label} · {role.count}
            </Chip>
          ))}
        </Group>
      </Chip.Group>

      {filtered.length === 0 && (
        <Text size="sm" c="dimmed" py="md"><Trans>No shows match.</Trans></Text>
      )}

      <ScrollArea.Autosize mah={460} scrollbars="y" style={{ overflowX: 'hidden' }}>
        <Stack gap={4} style={{ minWidth: 0 }}>
          {filtered.map((entry) => (
            <AnimeSignalRow key={entry.key} entry={entry} />
          ))}
        </Stack>
      </ScrollArea.Autosize>

      <Text size="xs" c="dimmed">
        <Trans>Showing {shown} of {total}</Trans>
      </Text>
    </Stack>
  )
}

function SignalRow({ row, busy, onClear, onExclude }: {
  row: Row
  busy: boolean
  onClear: (row: Row, action: 'clear-suppression' | 'clear-exposure' | 'clear-sentiment') => void
  onExclude: (row: Row, ignoreAsSeed: boolean) => void
}) {
  const { t } = useLingui()
  const state = row.state
  const sentiment = state?.sentiment ?? 'none'
  const suppression = state?.suppression ?? 'none'
  const exposure = state?.exposure ?? []
  const rating = row.rating
  const media = exposure.join(', ')
  const until = state?.dismissedUntilUtc ? formatDate(state.dismissedUntilUtc) : ''
  const id = row.id
  const title = row.title || t`Catalogue title ${id}`

  // A rating of 4 or under is the same statement as a thumbs down, so it gets the same line. The
  // ceiling lives in RecommendationFeedbackPolicy.AvoidRatingCeiling; 5 is neutral.
  const keptOut = sentiment === 'disliked' || (rating !== null && rating <= 4)

  const why = row.excluded
    ? t`On your shelf, not used for ranking`
    : keptOut
      ? t`Pushes down titles close to this one`
      : suppression !== 'none'
        ? t`Hidden from recommendations`
        : exposure.length > 0
          ? t`Not recommended again`
          : t`Shapes recommendations`

  const action = row.excluded
    ? { label: t`Use for taste`, color: undefined, variant: 'outline', run: () => onExclude(row, false) }
    : suppression !== 'none'
      ? { label: t`Restore`, color: 'yellow', variant: 'outline', run: () => onClear(row, 'clear-suppression') }
      : exposure.length > 0
        ? { label: t`Clear seen`, color: undefined, variant: 'outline', run: () => onClear(row, 'clear-exposure') }
        : sentiment !== 'none'
          ? { label: t`Clear rating`, color: undefined, variant: 'outline', run: () => onClear(row, 'clear-sentiment') }
          : row.onShelf
            ? { label: t`Exclude from taste`, color: undefined, variant: 'subtle', run: () => onExclude(row, true) }
            : null

  const plain = !row.excluded && suppression === 'none' && sentiment === 'none' &&
    exposure.length === 0 && rating === null

  return (
    <Group gap="sm" wrap="nowrap" align="flex-start" py={10} className="signals-row">
      <SeriesThumb url={row.coverUrl} alt={title} large />
      <div style={{ flex: 1, minWidth: 0 }}>
        <Text size="sm" fw={500} truncate>{title}</Text>
        {row.genres.length > 0 && (
          <Text size="xs" c="dimmed" truncate>{row.genres.join(', ')}</Text>
        )}
      </div>
      <div style={{ width: 300, flex: 'none' }}>
        <Group gap={6} wrap="wrap">
          {rating !== null && (
            <Badge size="sm" variant="light" color={rating <= 4 ? 'red' : 'teal'}>
              {t`★ ${rating} rated`}
            </Badge>
          )}
          {sentiment === 'liked' && (
            <Pill color="teal" label={t`👍 Liked`} clear={t`Clear the thumbs up`}
              onClear={() => onClear(row, 'clear-sentiment')} />
          )}
          {sentiment === 'disliked' && (
            <Pill color="red" label={t`👎 Disliked`} clear={t`Clear the thumbs down`}
              onClear={() => onClear(row, 'clear-sentiment')} />
          )}
          {suppression === 'hidden' && (
            <Pill color="yellow" label={t`Hidden`} clear={t`Restore this title`}
              onClear={() => onClear(row, 'clear-suppression')} />
          )}
          {suppression === 'dismissed' && (
            <Pill color="yellow" label={t`Dismissed until ${until}`} clear={t`Restore this title`}
              onClear={() => onClear(row, 'clear-suppression')} />
          )}
          {exposure.length > 0 && (
            <Pill color="blue" label={t`Seen: ${media}`} clear={t`Clear read or seen`}
              onClear={() => onClear(row, 'clear-exposure')} />
          )}
          {row.excluded && (
            <Pill color="gray" label={t`Excluded from taste`} clear={t`Use this title for taste`}
              onClear={() => onExclude(row, false)} />
          )}
          {plain && row.isRead && <Badge size="sm" variant="light" color="gray">{t`Read`}</Badge>}
          {plain && !row.isRead && row.onShelf && (
            <Badge size="sm" variant="light" color="gray">{t`On shelf`}</Badge>
          )}
        </Group>
        <Text size="xs" c="dimmed" mt={4}>{why}</Text>
      </div>
      <div style={{ width: 140, flex: 'none', textAlign: 'right' }}>
        {action && (
          <Button
            size="xs" variant={action.variant} color={action.color} loading={busy}
            onClick={action.run}
          >
            {action.label}
          </Button>
        )}
      </div>
    </Group>
  )
}

/** A state badge with the × that clears exactly that state. */
function Pill({ color, label, clear, onClear }: {
  color: string
  label: string
  clear: string
  onClear: () => void
}) {
  return (
    <Badge
      size="sm" variant="light" color={color} pr={3}
      rightSection={
        <ActionIcon size={14} variant="transparent" color={color} aria-label={clear} onClick={onClear}>
          <IconX size={12} />
        </ActionIcon>
      }
    >
      {label}
    </Badge>
  )
}

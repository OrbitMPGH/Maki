import { useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import {
  ActionIcon,
  Alert,
  Button,
  Group,
  Menu,
  Select,
  Skeleton,
  Stack,
  Text,
  TextInput,
  Tooltip,
  VisuallyHidden,
} from '@mantine/core'
import {
  IconAlertCircle,
  IconBookmark,
  IconBookmarkFilled,
  IconDots,
  IconExternalLink,
  IconEyeOff,
  IconHistory,
  IconPencil,
  IconPlus,
  IconStarFilled,
  IconThumbDown,
} from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import {
  DISCOVER_SECTION_LABELS,
  HOME_SECTION_LABELS,
  useRootFolders,
  useSeriesIdLookup,
  type RecommendationItem,
} from '../../api/hooks'
import { errorText } from '../../api/errorText'
import { currentFeedbackRevision, useMutateFeedback } from '../../api/recommendationFeedback'
import { useSeriesRequests } from '../../api/requests'
import {
  useAcknowledgeShortlistChange,
  useShortlist,
  useUpdateShortlistNote,
  type ShortlistEntry,
} from '../../api/shortlist'
import { useAuth } from '../../auth/AuthProvider'
import { GENRE_LABELS, TYPE_LABELS } from '../../components/CatalogueFilters'
import { DiscoverDetailModal } from '../../components/discover/DiscoverDetailModal'
import { PreviewChapterButton } from '../../components/discover/PreviewChapterButton'
import { SeriesPreviewReader } from '../../components/discover/SeriesPreviewReader'
import { useRemoveWithUndo } from '../../components/discover/shortlistActions'
import { Disclosure } from '../../components/ui/Disclosure'
import { MetaList } from '../../components/ui/MetaList'
import { Panel } from '../../components/ui/Panel'
import { SectionHeader } from '../../components/ui/SectionHeader'
import { seriesStatusVisual, statusToken } from '../../components/ui/status'
import { TagChip, TagChips } from '../../components/ui/TagChip'
import { relativeTime } from '../../components/ui/time'
import { formatFixedDecimal, formatNumber } from '../../format'
import { useLabel } from '../../i18n-context'
import { usePageState } from '../../lib/pageState'
import { notifications } from '../../lib/toast'
import { randomUUID } from '../../lib/uuid'

type Filter = 'all' | 'changed' | 'completed' | 'anime'
type Sort = 'recent' | 'oldest' | 'score' | 'title'

const WAITED_MS = 180 * 24 * 60 * 60 * 1000

/** The catalogue's current title, or the one stored when the title was saved once the catalogue has none. */
function shownTitle(entry: ShortlistEntry): string {
  return entry.item?.title ?? entry.title
}

function isChanged(entry: ShortlistEntry): boolean {
  return entry.statusChange != null
}

/** The sentence for a status that moved since the title was saved. */
function useChangeLabel() {
  const { t } = useLingui()
  return (to: string): string => {
    switch (to) {
      case 'Completed':
        return t`Completed since you saved it`
      case 'Ongoing':
        return t`Resumed since you saved it`
      case 'Hiatus':
        return t`On hiatus since you saved it`
      case 'Cancelled':
        return t`Cancelled since you saved it`
      default:
        return t`Status changed since you saved it`
    }
  }
}

/** Discover's labels win the keys Home shares with it (`recent`, `following`). */
const RAIL_LABELS: Record<string, MessageDescriptor> = { ...HOME_SECTION_LABELS, ...DISCOVER_SECTION_LABELS }

/** "Saved 2 months ago from Trending now", with the source named the way the reader met it. */
function useSavedLine() {
  const { t } = useLingui()
  const label = useLabel()
  return (entry: ShortlistEntry): string => {
    const when = relativeTime(entry.createdAtUtc)
    const detail = entry.savedFromDetail
    const [kind, rest] = entry.savedFrom.split(':', 2)
    let source: string | null = null
    if (kind === 'rail') {
      const known = RAIL_LABELS[rest] as MessageDescriptor | undefined
      source = known ? label(known) : (detail ?? null)
    } else if (kind === 'search') {
      source = t`a search`
    } else if (kind === 'catalogue') {
      source = t`the catalogue`
    } else if (kind === 'creator') {
      source = detail ? t`the page for ${detail}` : t`a creator page`
    } else if (kind === 'series') {
      source = t`a series page`
    } else if (kind === 'anime') {
      source = t`Continue from the anime`
    } else if (kind === 'importlist') {
      source = detail ? t`your ${detail} list` : t`an import list`
    }
    return source ? t`Saved ${when} from ${source}` : t`Saved ${when}`
  }
}

function ShortlistRow({
  entry,
  canAdd,
  requested,
  onOpen,
  onPreview,
}: {
  entry: ShortlistEntry
  canAdd: boolean
  requested: boolean
  onOpen: (item: RecommendationItem) => void
  onPreview: (entry: ShortlistEntry) => void
}) {
  const { t } = useLingui()
  const label = useLabel()
  const queryClient = useQueryClient()
  const changeLabel = useChangeLabel()
  const savedLine = useSavedLine()
  const removeWithUndo = useRemoveWithUndo()
  const acknowledge = useAcknowledgeShortlistChange()
  const updateNote = useUpdateShortlistNote()
  const feedback = useMutateFeedback()
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState('')
  const cancelled = useRef(false)
  const editAfterMenu = useRef(false)

  const { item, library, statusChange, newChapters, note, type } = entry
  const shown = shownTitle(entry)
  const title = shown
  const inLibrary = library != null
  const ownedId = library?.seriesId ?? null
  const feedbackId = Number(item?.providerId ?? entry.mangaBakaId)

  const startEdit = () => {
    setDraft(note ?? '')
    cancelled.current = false
    setEditing(true)
  }
  const commitNote = () => {
    if (cancelled.current) return
    cancelled.current = true
    setEditing(false)
    if (draft.trim() !== (note ?? '').trim()) updateNote.mutate({ id: entry.mangaBakaId, note: draft })
  }

  const leave = async (action: 'dislike' | 'hide') => {
    try {
      const expectedRevision = await currentFeedbackRevision(queryClient, feedbackId)
      await feedback.mutateAsync({ id: feedbackId, action, expectedRevision, clientMutationId: randomUUID() })
      notifications.show({
        message:
          action === 'dislike'
            ? t`Thumbs down. ${title} is out of your recommendations and left your shortlist.`
            : t`${title} is hidden from your recommendations and left your shortlist.`,
      })
    } catch (error) {
      const reason = errorText(error)
      notifications.show({ color: 'var(--danger)', message: t`Could not update feedback: ${reason}` })
    }
  }

  const change = statusChange ? seriesStatusVisual(statusChange.to) : null
  const totalChapters = item?.totalChapters ?? null
  const meta = item
    ? [
        type ? label(TYPE_LABELS[type] ?? type) : null,
        item.year,
        label(seriesStatusVisual(item.status).label),
        totalChapters ? t`${totalChapters} ch.` : null,
      ]
    : []
  const genres = item ? item.matchedGenres.slice(0, 3) : []

  const cover = item ? (item.thumbUrlHiDpi ?? item.thumbUrl ?? item.coverUrl) : null
  const coverNode = cover ? (
    <img src={cover} alt="" loading="lazy" decoding="async" />
  ) : (
    <div className="cover-placeholder" aria-hidden>
      {shown}
    </div>
  )

  return (
    <Panel className="shortlist-row" p="md" data-changed={isChanged(entry) || undefined}>
      {item ? (
        <button type="button" className="shortlist-cover" aria-label={t`View ${title}`} onClick={() => onOpen(item)}>
          {coverNode}
        </button>
      ) : (
        <div className="shortlist-cover" data-gone="true">
          {coverNode}
        </div>
      )}

      <div className="shortlist-body">
        <div className="shortlist-head">
          {item ? (
            <button type="button" className="shortlist-title" onClick={() => onOpen(item)}>
              {shown}
            </button>
          ) : (
            <span className="shortlist-title">{shown}</span>
          )}
          {item?.rating != null && (
            <span className="shortlist-score tnum">
              <IconStarFilled size={16} style={{ color: 'var(--rating)' }} />
              {formatFixedDecimal(item.rating / 10, 1)}
            </span>
          )}
        </div>

        {item ? (
          <Text component="div" size="sm" c="var(--ink-3)">
            <MetaList items={meta} />
          </Text>
        ) : (
          <Text size="sm" c="var(--ink-3)">
            <Trans>The catalogue no longer lists this title. You can still remove it.</Trans>
          </Text>
        )}

        {genres.length > 0 && (
          <TagChips>
            {genres.map((g) => (
              <TagChip key={g} size="sm" dot="var(--neutral)">
                {label(GENRE_LABELS[g] ?? g)}
              </TagChip>
            ))}
          </TagChips>
        )}

        {(change || (newChapters != null && newChapters > 0)) && (
          <Group gap="xs" wrap="wrap">
            {change && statusChange && (
              <>
                <span
                  className="shortlist-pill"
                  style={{
                    color: `var(--${statusToken(change.color)})`,
                    background: `var(--${statusToken(change.color)}-soft)`,
                  }}
                >
                  <change.Icon size={13} />
                  {changeLabel(statusChange.to)}
                </span>
                <Button
                  size="compact-xs"
                  variant="subtle"
                  loading={acknowledge.isPending}
                  onClick={() => acknowledge.mutate({ id: entry.mangaBakaId })}
                >
                  <Trans>Got it</Trans>
                </Button>
              </>
            )}
            {newChapters != null && newChapters > 0 && (
              <span className="shortlist-pill" data-quiet="true">
                {plural(newChapters, { one: '+# chapter', other: '+# chapters' })}
              </span>
            )}
          </Group>
        )}

        {editing ? (
          <TextInput
            size="xs"
            autoFocus
            aria-label={t`Note for later`}
            placeholder={t`Note for later`}
            maxLength={500}
            value={draft}
            onChange={(e) => setDraft(e.currentTarget.value)}
            onBlur={commitNote}
            onKeyDown={(e) => {
              if (e.key === 'Enter') commitNote()
              if (e.key === 'Escape') {
                cancelled.current = true
                setEditing(false)
              }
            }}
          />
        ) : note ? (
          <div className="shortlist-note">
            <Text size="sm" c="var(--ink-2)">
              {note}
            </Text>
            <ActionIcon size="sm" variant="subtle" color="var(--neutral)" aria-label={t`Edit note`} onClick={startEdit}>
              <IconPencil size={16} />
            </ActionIcon>
          </div>
        ) : null}

        <Text size="xs" c="var(--ink-4)">
          {savedLine(entry)}
        </Text>
      </div>

      <div className="shortlist-actions">
        {item && !inLibrary && <PreviewChapterButton providerId={item.providerId} title={title} onRead={() => onPreview(entry)} />}

        {item && !inLibrary && requested && (
          <span className="shortlist-pill" data-quiet="true">
            <Trans>Requested</Trans>
          </span>
        )}
        {item && !inLibrary && !requested && (
          <Button leftSection={<IconPlus size={16} />} onClick={() => onOpen(item)}>
            {canAdd ? <Trans>Add to library</Trans> : <Trans>Request</Trans>}
          </Button>
        )}

        {inLibrary && (
          <Group gap="xs" wrap="wrap">
            <span className="shortlist-pill" data-quiet="true">
              {library.archived ? <Trans context="series">Archived</Trans> : <Trans>In the library</Trans>}
            </span>
            {ownedId != null && (
              <Button component={Link} to={`/series/${ownedId}`} variant="default" size="xs">
                <Trans>Open series</Trans>
              </Button>
            )}
          </Group>
        )}

        <Group gap="xs" wrap="nowrap" justify="flex-end">
          <Tooltip label={t`Remove from shortlist`} withArrow>
            <ActionIcon
              className="shortlist-ribbon"
              size="lg"
              variant="subtle"
              color="var(--brand)"
              aria-label={t`Remove ${title} from your shortlist`}
              onClick={() => removeWithUndo(entry.mangaBakaId, title)}
            >
              <IconBookmarkFilled size={20} />
            </ActionIcon>
          </Tooltip>

          {item && !inLibrary && (
            <Menu withinPortal position="bottom-end" width={220}
              transitionProps={{
                onExited: () => {
                  if (!editAfterMenu.current) return
                  editAfterMenu.current = false
                  startEdit()
                },
              }}
            >
              <Menu.Target>
                <ActionIcon size="lg" variant="subtle" color="var(--neutral)" aria-label={t`More actions for ${title}`}>
                  <IconDots size={18} />
                </ActionIcon>
              </Menu.Target>
              <Menu.Dropdown>
                <Menu.Item leftSection={<IconPencil size={16} />} onClick={() => (editAfterMenu.current = true)}>
                  <Trans>Edit note</Trans>
                </Menu.Item>
                <Menu.Item leftSection={<IconThumbDown size={16} />} onClick={() => void leave('dislike')}>
                  <Trans>Not for me</Trans>
                </Menu.Item>
                <Menu.Item leftSection={<IconEyeOff size={16} />} onClick={() => void leave('hide')}>
                  <Trans>Hide this title</Trans>
                </Menu.Item>
                <Menu.Divider />
                <Menu.Item
                  component="a"
                  href={`https://mangabaka.org/${item.providerId}`}
                  target="_blank"
                  rel="noopener noreferrer"
                  leftSection={<IconExternalLink size={16} />}
                >
                  <Trans>Open on MangaBaka</Trans>
                </Menu.Item>
              </Menu.Dropdown>
            </Menu>
          )}
        </Group>
      </div>
    </Panel>
  )
}

function Comparison() {
  const { t } = useLingui()
  const rows: [string, string, string, string][] = [
    [t`Source search, chapter sync, downloads`, t`No`, t`Yes`, t`No`],
    [t`Seeds or avoids in recommendations`, t`No`, t`Yes`, t`Yes`],
    [t`Stats, Rewind, XP`, t`No`, t`Yes`, t`No`],
    [t`Still shown in Discover rails`, t`Yes, marked`, t`As owned`, t`No`],
    [t`Notifications of any kind`, t`No`, t`Per chapter`, t`No`],
  ]
  return (
    <table className="shortlist-compare">
      <thead>
        <tr>
          <th scope="col">
            <VisuallyHidden>
              <Trans>What it does</Trans>
            </VisuallyHidden>
          </th>
          <th scope="col">
            <Trans>Shortlist</Trans>
          </th>
          <th scope="col">
            <Trans>Library</Trans>
          </th>
          <th scope="col">
            <Trans>Thumbs up</Trans>
          </th>
        </tr>
      </thead>
      <tbody>
        {rows.map(([what, shortlist, library, thumbs]) => (
          <tr key={what}>
            <th scope="row">{what}</th>
            <td>{shortlist}</td>
            <td>{library}</td>
            <td>{thumbs}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function EmptyShortlist() {
  const [open, setOpen] = useState(false)
  return (
    <div className="shortlist-empty">
      <span className="shortlist-empty-disc" aria-hidden>
        <IconBookmark size={28} />
      </span>
      <Text className="empty-state-title">
        <Trans>Nothing on your shortlist</Trans>
      </Text>
      <Text c="var(--ink-3)" size="sm" maw={460} ta="center">
        <Trans>
          Use the bookmark on any cover in Discover, or Shortlist on its card, to keep a title here without adding it.
        </Trans>
      </Text>
      <Button component={Link} to="/discover" variant="default">
        <Trans>Browse the catalogue</Trans>
      </Button>
      <div className="shortlist-empty-compare">
        <Disclosure title={<Trans>How the shortlist differs from your library</Trans>} opened={open} onToggle={() => setOpen((o) => !o)}>
          <Comparison />
        </Disclosure>
      </div>
    </div>
  )
}

function ShortlistSkeleton() {
  return (
    <Stack gap="sm" aria-hidden inert>
      {[0, 1, 2].map((i) => (
        <Panel key={i} p="md">
          <Group wrap="nowrap" align="flex-start" gap="md">
            <Skeleton w={76} h={114} radius="sm" />
            <Stack gap={8} style={{ flex: 1 }}>
              <Skeleton h={16} w="40%" />
              <Skeleton h={12} w="55%" />
              <Skeleton h={12} w="30%" />
            </Stack>
          </Group>
        </Panel>
      ))}
    </Stack>
  )
}

/** Discover's fourth tab: titles kept for later, with what changed since they were saved. */
export function ShortlistTab() {
  const { t, i18n } = useLingui()
  const { can } = useAuth()
  const canAdd = can('AddSeries')
  const { data, isPending, isError, error, refetch } = useShortlist()
  const entries = data?.entries
  const { data: requests } = useSeriesRequests('all', !canAdd)
  const { data: rootFolders } = useRootFolders()
  const seriesIdFor = useSeriesIdLookup()
  const [filter, setFilter] = usePageState<Filter>('shortlist:filter', 'all')
  const [sort, setSort] = usePageState<Sort>('shortlist:sort', 'recent')
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)
  const [preview, setPreview] = useState<ShortlistEntry | null>(null)
  const previewItem = preview?.item ?? null

  const requestedIds = useMemo(
    () =>
      new Set(
        (requests ?? [])
          .filter((r) => r.kind === 'NewSeries' && (r.status === 'Pending' || r.status === 'Processing'))
          .map((r) => r.metadataProviderId),
      ),
    [requests],
  )

  const all = entries ?? []
  const counts = {
    all: all.length,
    changed: all.filter(isChanged).length,
    completed: all.filter((e) => e.item?.status === 'Completed').length,
    anime: all.filter((e) => e.hasAnime).length,
  }
  const waited = all.filter((e) => Date.now() - new Date(e.createdAtUtc).getTime() > WAITED_MS).length

  const { changedRows, restRows } = useMemo(() => {
    const keep = (e: ShortlistEntry) => {
      switch (filter) {
        case 'changed':
          return isChanged(e)
        case 'completed':
          return e.item?.status === 'Completed'
        case 'anime':
          return e.hasAnime
        default:
          return true
      }
    }
    const byTitle = (e: ShortlistEntry) => shownTitle(e)
    const time = (e: ShortlistEntry) => new Date(e.createdAtUtc).getTime()
    const compare = (a: ShortlistEntry, b: ShortlistEntry) => {
      switch (sort) {
        case 'oldest':
          return time(a) - time(b)
        case 'score':
          return (b.item?.rating ?? -1) - (a.item?.rating ?? -1)
        case 'title':
          return byTitle(a).localeCompare(byTitle(b), i18n.locale)
        default:
          return time(b) - time(a)
      }
    }
    const shown = (entries ?? []).filter(keep).sort(compare)
    return { changedRows: shown.filter(isChanged), restRows: shown.filter((e) => !isChanged(e)) }
  }, [entries, filter, sort, i18n.locale])

  const filterOptions: { value: Filter; label: string }[] = [
    { value: 'all', label: t`All` },
    { value: 'changed', label: t`Changed since saved` },
    { value: 'completed', label: t`Completed` },
    { value: 'anime', label: t`Has an anime` },
  ]
  const sortOptions: { value: Sort; label: string }[] = [
    { value: 'recent', label: t`Recently saved` },
    { value: 'oldest', label: t`Oldest first` },
    { value: 'score', label: t`Score` },
    { value: 'title', label: t`Title` },
  ]

  if (isPending) return <ShortlistSkeleton />
  if (isError) {
    const reason = errorText(error)
    return (
      <Alert color="var(--danger)" variant="light" icon={<IconAlertCircle size={16} />} title={t`Could not load your shortlist`}>
        <Group gap="sm">
          <Text size="sm">{reason}</Text>
          <Button size="xs" variant="default" onClick={() => void refetch()}>
            <Trans>Retry</Trans>
          </Button>
        </Group>
      </Alert>
    )
  }
  if (data?.catalogueAvailable === false) {
    return (
      <Alert color="var(--warn)" variant="light" icon={<IconAlertCircle size={16} />} title={t`Discover's catalogue is not downloaded yet`}>
        <Text size="sm">
          <Trans>Anything you saved is kept and shows up here once the catalogue is installed.</Trans>
        </Text>
      </Alert>
    )
  }
  if (all.length === 0) return <EmptyShortlist />

  const rowProps = (e: ShortlistEntry) => ({
    entry: e,
    canAdd,
    requested: e.item != null && requestedIds.has(e.item.providerId),
    onOpen: setDetailItem,
    onPreview: setPreview,
  })

  return (
    <Stack gap="lg">
      <Text c="var(--ink-3)" size="sm">
        <Trans>
          Titles you want to come back to. Nothing here is downloaded or counted in your taste until you add it.
        </Trans>
      </Text>

      <div className="shortlist-controls">
        <TagChips>
          {filterOptions.map((o) =>
            counts[o.value] === 0 && o.value !== 'all' && filter !== o.value ? null : (
              <TagChip key={o.value} active={filter === o.value} aria-pressed={filter === o.value} onClick={() => setFilter(o.value)}>
                {o.label} <span className="tnum shortlist-chip-count">{formatNumber(counts[o.value])}</span>
              </TagChip>
            ),
          )}
        </TagChips>
        <Select
          size="xs"
          w={170}
          aria-label={t`Sort`}
          data={sortOptions}
          value={sort}
          allowDeselect={false}
          onChange={(v) => v && setSort(v as Sort)}
        />
      </div>

      {changedRows.length > 0 && (
        <Stack gap="sm">
          <SectionHeader icon={IconHistory} title={t`Changed since you saved them`} count={changedRows.length} />
          {changedRows.map((e) => (
            <ShortlistRow key={e.mangaBakaId} {...rowProps(e)} />
          ))}
        </Stack>
      )}

      {restRows.length > 0 && (
        <Stack gap="sm">
          <SectionHeader icon={IconBookmark} title={t`Waiting for you`} count={restRows.length} />
          {restRows.map((e) => (
            <ShortlistRow key={e.mangaBakaId} {...rowProps(e)} />
          ))}
        </Stack>
      )}

      {changedRows.length + restRows.length === 0 && (
        <Text c="var(--ink-3)" size="sm">
          <Trans>Nothing on your shortlist matches this filter.</Trans>
        </Text>
      )}

      {filter === 'all' && sort !== 'oldest' && waited > 0 && (
        <div className="shortlist-waited">
          <Text size="sm" c="var(--ink-3)">
            {plural(waited, {
              one: '# title has waited more than six months.',
              other: '# titles have waited more than six months.',
            })}
          </Text>
          <Button size="compact-sm" variant="subtle" onClick={() => setSort('oldest')}>
            <Trans>Show the oldest</Trans>
          </Button>
        </div>
      )}

      <DiscoverDetailModal
        item={detailItem}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
      />
      {previewItem && (
        <SeriesPreviewReader
          providerId={previewItem.providerId}
          title={previewItem.title}
          coverUrl={previewItem.coverUrl ?? previewItem.thumbUrlHiDpi ?? previewItem.thumbUrl}
          seriesType={preview?.type ?? null}
          onClose={() => setPreview(null)}
        />
      )}
    </Stack>
  )
}

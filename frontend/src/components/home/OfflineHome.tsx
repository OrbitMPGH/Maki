import { useMemo } from 'react'
import { Link } from 'react-router-dom'
import { Button } from '@mantine/core'
import { useQueries } from '@tanstack/react-query'
import { plural } from '@lingui/core/macro'
import { Trans, useLingui } from '@lingui/react/macro'
import { IconCloudDownload, IconSettings } from '@tabler/icons-react'
import { api } from '../../api/client'
import type { HomeReadingItem } from '../../api/hooks'
import type { ReaderManifest } from '../../api/reader'
import type { OfflineChapter } from '../../pwa/contract'
import { EmptyState } from '../ui/EmptyState'
import { Rail } from '../ui/Rail'
import { RailSkeleton } from '../ui/RailSkeleton'
import { SectionHeader } from '../ui/SectionHeader'
import { ReadingCard } from './ReadingRail'

const OFFLINE_SETTINGS = '/settings?tab=reading&s=offline-reading'

interface SavedSeries {
  item: HomeReadingItem
  saved: number
  latestSave: string
}

/**
 * Home while the server is out of reach and Home's own data never made it onto this device. What
 * is left is what was saved for offline reading, one card per series. Each card opens the first
 * saved chapter not yet finished, read off the manifest stored with it, which is also where the
 * title and cover come from: the library list is exactly what is missing here.
 */
export function OfflineHome({
  supported,
  controlled,
  records,
  onRetry,
}: {
  /** False over plain HTTP, where nothing can be saved on the device. */
  supported: boolean
  /** False when no service worker answers this tab, so nothing saved can be served to it. */
  controlled: boolean
  records: ReadonlyMap<number, OfflineChapter>
  onRetry: () => void
}) {
  const { t } = useLingui()
  const saved = useMemo(
    () => [...records.values()].filter((r) => r.state === 'saved' || r.state === 'stale'),
    [records],
  )

  // Its own key: the reader's manifest query must never be seeded from a copy read here.
  const manifests = useQueries({
    queries: (controlled ? saved : []).map((r) => ({
      queryKey: ['offline-home', 'manifest', r.chapterId],
      queryFn: () => api<ReaderManifest>(`/reader/chapter/${r.chapterId}`),
      staleTime: Infinity,
      retry: false,
      meta: { silent: true },
    })),
    combine: (results) => ({
      data: results.flatMap((r) => (r.data ? [r.data] : [])),
      pending: results.some((r) => r.isPending),
    }),
  })

  const series = useMemo(() => bySeries(saved, manifests.data), [saved, manifests.data])

  if (!supported || saved.length === 0) {
    return (
      <EmptyState
        art="offline"
        headingOrder={2}
        title={t`You're offline`}
        description={
          supported ? (
            <Trans>
              Nothing is saved on this device yet. Once you're back online, save chapters from a series
              page and they'll be here to read without a connection.
            </Trans>
          ) : (
            <Trans>Maki can't reach the server. Home comes back as soon as it does.</Trans>
          )
        }
        actionLabel={t`Try again`}
        onAction={onRetry}
        secondaryActionLabel={supported ? t`Offline reading` : undefined}
        secondaryActionTo={OFFLINE_SETTINGS}
        secondaryActionIcon={<IconSettings size={16} />}
      />
    )
  }

  if (!controlled) {
    const count = saved.length
    return (
      <EmptyState
        art="offline"
        headingOrder={2}
        title={t`You're offline`}
        description={plural(count, {
          one: "A chapter is saved on this device, but this tab can't open it right now. That usually follows a hard reload: reload the page normally and it will be ready to read.",
          other: "# chapters are saved on this device, but this tab can't open them right now. That usually follows a hard reload: reload the page normally and they will be ready to read.",
        })}
        actionLabel={t`Reload`}
        onAction={() => window.location.reload()}
        secondaryActionLabel={t`Offline reading`}
        secondaryActionTo={OFFLINE_SETTINGS}
        secondaryActionIcon={<IconSettings size={16} />}
      />
    )
  }

  return (
    <>
      <EmptyState
        mood="asleep"
        title={t`You're offline`}
        description={t`What's new on the server can wait. These chapters are saved on this device and ready to read.`}
        actionLabel={t`Try again`}
        onAction={onRetry}
      />
      <SectionHeader
        icon={IconCloudDownload}
        title={t`Saved on this device`}
        count={series.length || undefined}
        action={
          <Button component={Link} to={OFFLINE_SETTINGS} variant="subtle" size="compact-sm">
            <Trans>Manage</Trans>
          </Button>
        }
        chevron
      />
      {series.length === 0 && manifests.pending ? (
        <RailSkeleton />
      ) : series.length === 0 ? (
        <EmptyState
          title={t`The saved chapters wouldn't open`}
          description={t`They are still on this device. Reload the page, or remove and save them again once you're back online.`}
          actionLabel={t`Reload`}
          onAction={() => window.location.reload()}
        />
      ) : (
        <Rail>
          {series.map(({ item, saved: count }) => (
            <div key={item.seriesId} className="discover-rail-item reading-card">
              <ReadingCard item={item} note={plural(count, { one: '# saved', other: '# saved' })} />
            </div>
          ))}
        </Rail>
      )}
    </>
  )
}

/** One card per series: the first saved chapter not finished, else the first saved. In progress first, then newest saves. */
function bySeries(records: OfflineChapter[], manifests: ReaderManifest[]): SavedSeries[] {
  const savedAt = new Map(records.map((r) => [r.chapterId, r.savedAt]))
  const groups = new Map<number, ReaderManifest[]>()
  for (const m of manifests) {
    const list = groups.get(m.seriesId) ?? []
    list.push(m)
    groups.set(m.seriesId, list)
  }

  const out: SavedSeries[] = []
  for (const list of groups.values()) {
    list.sort((a, b) => (a.number ?? Infinity) - (b.number ?? Infinity) || a.chapterId - b.chapterId)
    const next = list.find((m) => !m.completed) ?? list[0]
    out.push({
      item: {
        seriesId: next.seriesId,
        seriesTitle: next.seriesTitle,
        coverUrl: next.seriesCoverUrl,
        chapterId: next.chapterId,
        chapterLabel: next.label,
        page: next.completed ? 0 : next.resumePage,
        pageCount: next.pageCount,
        lastReadAt: '',
        unreadChapters: 0,
        coverAccent: null,
      },
      saved: list.length,
      latestSave: list.reduce((acc, m) => {
        const at = savedAt.get(m.chapterId) ?? ''
        return at > acc ? at : acc
      }, ''),
    })
  }

  return out.sort(
    (a, b) => Number(b.item.page > 0) - Number(a.item.page > 0) || b.latestSave.localeCompare(a.latestSave),
  )
}

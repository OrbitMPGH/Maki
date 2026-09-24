import { useState } from 'react'
import { SimpleGrid, Skeleton, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { Plural, useLingui } from '@lingui/react/macro'
import { usePlanToRead, usePlanToReadRemove, type PlanToReadEntry } from '../api/planToRead'
import { useRootFolders, type RecommendationItem } from '../api/hooks'
import { DiscoverDetailModal } from '../components/discover/DiscoverDetailModal'
import { ShortlistCard } from '../components/discover/ShortlistCard'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'

/** Same column count as the Library grid from `sm` up; forced to two below it (see App requirements). */
const SHORTLIST_COLS = { base: 2, xs: 2, sm: 3, md: 4, lg: 5, xl: 6 }

/** Builds a card-shaped item from what the entry itself carries. `DiscoverDetailModal` hydrates the
 *  rest through `useRecommendationDetail`, so every other field only needs to be present, not accurate. */
function entryAsItem(entry: PlanToReadEntry): RecommendationItem {
  return {
    providerId: String(entry.providerId),
    title: entry.title,
    coverUrl: entry.coverUrl,
    thumbUrl: null,
    thumbUrlHiDpi: null,
    year: null,
    description: null,
    status: '',
    rating: null,
    totalChapters: null,
    matchedGenres: [],
    matchedTags: [],
    authorMatch: false,
    relationKind: null,
    relatedToTitle: null,
    becauseOfTitle: null,
  }
}

function ShortlistSkeletons() {
  return (
    <SimpleGrid cols={SHORTLIST_COLS} spacing="md" aria-hidden>
      {Array.from({ length: 12 }, (_, i) => (
        <div key={i} className="shortlist-card">
          <Skeleton radius="lg" style={{ aspectRatio: '2 / 3' }} />
          <div className="shortlist-body">
            <Skeleton h={14} w="80%" mb="sm" />
            <div className="shortlist-actions">
              <Skeleton h={28} style={{ flex: 1 }} />
              <Skeleton h={28} style={{ flex: 1 }} />
            </div>
          </div>
        </div>
      ))}
    </SimpleGrid>
  )
}

export default function ShortlistPage() {
  const { t } = useLingui()
  const { data: entries, isLoading } = usePlanToRead()
  const remove = usePlanToReadRemove()
  const rootFolders = useRootFolders().data
  const [detailEntry, setDetailEntry] = useState<PlanToReadEntry | null>(null)

  const sorted = [...(entries ?? [])].sort(
    (a, b) => new Date(b.addedAtUtc).getTime() - new Date(a.addedAtUtc).getTime(),
  )

  async function removeEntry(providerId: number) {
    try {
      await remove.mutateAsync(providerId)
    } catch (error) {
      notifications.show({ color: 'var(--danger)', message: String(error) })
    }
  }

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      <PageHeader
        title={t`Shortlist`}
        description={t`Titles you picked in the Queue and from Discover, waiting for a decision.`}
        actions={
          !isLoading ? (
            <Text size="sm" c="var(--ink-3)" className="tnum">
              <Plural value={sorted.length} one="# title" other="# titles" />
            </Text>
          ) : undefined
        }
      />

      {isLoading && <ShortlistSkeletons />}

      {!isLoading && sorted.length === 0 && (
        <EmptyState
          title={t`Nothing on your shortlist yet`}
          description={t`Swipe right in the Queue, or save a title from Discover, and it lands here.`}
          actionLabel={t`Go to the Queue`}
          actionTo="/discover/queue"
        />
      )}

      {sorted.length > 0 && (
        <SimpleGrid cols={SHORTLIST_COLS} spacing="md">
          {sorted.map((entry) => (
            <ShortlistCard
              key={entry.providerId}
              entry={entry}
              onOpenDetail={() => setDetailEntry(entry)}
              onRemove={() => void removeEntry(entry.providerId)}
              removing={remove.isPending && remove.variables === entry.providerId}
            />
          ))}
        </SimpleGrid>
      )}

      <DiscoverDetailModal
        item={detailEntry ? entryAsItem(detailEntry) : null}
        inLibrarySeriesId={detailEntry?.inLibrarySeriesId}
        rootFolders={rootFolders}
        onClose={() => setDetailEntry(null)}
      />
    </SurfaceFrame>
  )
}

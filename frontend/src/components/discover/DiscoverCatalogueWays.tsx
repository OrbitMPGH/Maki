import { useMemo, useState, type ReactNode } from 'react'
import { Button, Group, SimpleGrid, Text } from '@mantine/core'
import { useIntersection } from '@mantine/hooks'
import { useNavigate } from 'react-router-dom'
import type { MessageDescriptor } from '@lingui/core'
import { msg } from '@lingui/core/macro'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import {
  useDiscoverCount,
  useDiscoverFeed,
  type BrowseSort,
  type RecommendationFilters,
  type RecommendationItem,
} from '../../api/hooks'
import { sectionSource } from '../../api/shortlist'
import { useLabel } from '../../i18n-context'
import { IconChevronRight } from '../../lib/icons'
import { RecommendationCard, type OpenItem } from '../ui/DiscoverRail'
import { TagChip } from '../ui/TagChip'
import { GENRE_LABELS, normalizeFilters, useCatalogueFilters, useLiveApply } from '../CatalogueFilters'
import { CatalogueFilterBar, useCatalogueFilterSlots } from '../CatalogueFilterBar'
import { CATALOGUE_PAGE, useWholeRows } from '../../lib/wholeRows'
import { FilterMatchCount, normalizeTerms } from '../CatalogueRules'
import type { CatalogueSeed } from '../CatalogueBrowser'

interface Way {
  key: string
  title: MessageDescriptor
  detail: MessageDescriptor
  filters: RecommendationFilters
  sort: BrowseSort
  excludeOwned?: boolean
}

const THIS_YEAR = new Date().getFullYear()

/** Four ways into the catalogue that are a question a reader can answer, each a plain filter. */
const WAYS: Way[] = [
  {
    key: 'short',
    title: msg`Short and finished`,
    detail: msg`Complete, up to 50 chapters`,
    filters: { statuses: ['completed'], maxChapters: 50 },
    sort: 'popular',
  },
  {
    key: 'long',
    title: msg`Long and finished`,
    detail: msg`Complete, over 150 chapters`,
    filters: { statuses: ['completed'], minChapters: 150 },
    sort: 'popular',
  },
  {
    key: 'new',
    title: msg`New this year`,
    detail: msg`First released this year`,
    filters: { yearMin: THIS_YEAR },
    sort: 'popular',
  },
  {
    key: 'top',
    title: msg`Top rated`,
    detail: msg`8.5 and up, not in your library`,
    filters: { minRating: 85 },
    sort: 'rating',
    excludeOwned: true,
  },
]

/** The genres offered as chips; the rest are one click away in the filter bar. */
const GENRES = ['Romance', 'Action', 'Fantasy', 'Comedy', 'Mystery', 'Horror', 'Slice of Life', 'Sci-Fi', 'Sports', 'Historical']

/** A filter as the bar rebuilds it (genres folded into rules), so a chosen way still matches once live apply runs. */
const normal = (f: RecommendationFilters) => normalizeTerms(normalizeFilters(f))
const keyOf = (f: RecommendationFilters) =>
  JSON.stringify(Object.fromEntries(Object.entries(normal(f)).sort(([a], [b]) => a.localeCompare(b))))

/** The one genre a filter holds and nothing else, or null. */
function onlyGenre(f: RecommendationFilters): string | null {
  const n = normal(f)
  const rule = n.rules?.[0]
  if (Object.keys(n).length !== 1 || n.rules?.length !== 1 || !rule) return null
  const term = rule.terms[0]
  return rule.mode === 'all' && rule.terms.length === 1 && term.kind === 'genre' ? term.name : null
}

/**
 * The top of "Browse the catalogue": four ways in, a row of genres and the catalogue's own filter
 * bar, all applied to the grid below in place. With nothing chosen the section shows its usual
 * feeds (`children`); any filter swaps in the matching titles, and "Show all" carries the filter to
 * the Add page, where the catalogue pages without limit.
 */
export function DiscoverCatalogueWays({
  cols,
  seriesIdFor,
  onOpen,
  children,
}: {
  cols: Record<string, number>
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: OpenItem
  /** The unfiltered catalogue: the feed tabs and their grid. */
  children: ReactNode
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const navigate = useNavigate()
  const catalogue = useCatalogueFilters()
  const slots = useCatalogueFilterSlots(catalogue.controls)
  const [applied, setApplied] = useState<RecommendationFilters>({})
  useLiveApply(catalogue.build, setApplied)
  const [way, setWay] = useState<Way | null>(null)

  // The doors' covers and counts are fetched once the section comes near the screen, not on load.
  const { ref, entry } = useIntersection<HTMLDivElement>({ rootMargin: '400px' })
  const [seen, setSeen] = useState(false)
  if (!seen && entry?.isIntersecting) setSeen(true)

  const appliedKey = keyOf(applied)
  const filtered = appliedKey !== '{}'
  // A way stays chosen only while the filter still is what it set.
  const activeWay = way && keyOf(way.filters) === appliedKey ? way : null
  const sort: BrowseSort = activeWay?.sort ?? 'popular'
  const excludeOwned = activeWay?.excludeOwned ?? false
  const activeGenre = activeWay ? null : onlyGenre(applied)

  const apply = (filters: RecommendationFilters, next: Way | null) => {
    const f = normal(filters)
    catalogue.hydrate(f)
    setApplied(f)
    setWay(next)
  }
  const clear = () => {
    catalogue.reset()
    setApplied({})
    setWay(null)
  }

  const request = useMemo(
    () => (filtered ? { feed: 'Popular', filters: applied, limit: CATALOGUE_PAGE, sort, excludeOwned } : null),
    [filtered, applied, sort, excludeOwned],
  )
  const results = useDiscoverFeed(request, true, true)
  const shown = useWholeRows(results.data ?? [], cols, CATALOGUE_PAGE)
  const seed: CatalogueSeed = { sort, filters: applied }

  return (
    <div ref={ref}>
      <div className="discover-ways">
        {WAYS.map((w) => (
          <WayDoor
            key={w.key}
            way={w}
            load={seen}
            active={activeWay?.key === w.key}
            onClick={() => (activeWay?.key === w.key ? clear() : apply(w.filters, w))}
          />
        ))}
      </div>

      <div className="discover-genres" role="group" aria-label={t`Genres`}>
        <span className="discover-genres-label">
          <Trans>Genre</Trans>
        </span>
        {GENRES.map((g) => (
          <TagChip
            key={g}
            size="md"
            active={activeGenre === g}
            aria-pressed={activeGenre === g}
            onClick={() => (activeGenre === g ? clear() : apply({ genres: [g] }, null))}
          >
            {renderLabel(GENRE_LABELS[g] ?? g)}
          </TagChip>
        ))}
      </div>

      <CatalogueFilterBar
        slots={slots}
        isCustomized={catalogue.isCustomized || filtered}
        onReset={clear}
        menu={false}
        count={filtered ? <FilterMatchCount filters={applied} /> : undefined}
      />

      {!filtered ? (
        children
      ) : (
        <>
          <Group justify="space-between" mt="md" mb="sm">
            <Text c="var(--ink-3)" size="sm">
              {sort === 'rating' ? <Trans>Top rated first</Trans> : <Trans>Most popular first</Trans>}
            </Text>
            <Button
              variant="subtle"
              size="xs"
              rightSection={<IconChevronRight size={16} />}
              onClick={() => navigate('/add', { state: { catalogueSeed: seed } })}
            >
              <Trans>Show all</Trans>
            </Button>
          </Group>
          {results.data && results.data.length === 0 ? (
            <Text c="var(--ink-3)" size="sm" py="lg">
              <Trans>No matches</Trans>
            </Text>
          ) : (
            <SimpleGrid cols={cols} spacing="md" className="discover-cat-grid" style={{ opacity: results.isFetching ? 0.6 : 1 }}>
              {shown.map((item) => (
                <RecommendationCard
                  key={item.providerId}
                  item={item}
                  inLibrarySeriesId={seriesIdFor(item)}
                  onOpen={onOpen}
                  reasonOverride={null}
                  savedFrom={sectionSource('catalogue')}
                />
              ))}
            </SimpleGrid>
          )}
        </>
      )}
    </div>
  )
}

/** One way in: three covers from its own results, fanned, then what it is and how many titles it holds. */
function WayDoor({ way, load, active, onClick }: { way: Way; load: boolean; active: boolean; onClick: () => void }) {
  const renderLabel = useLabel()
  const request = load
    ? { feed: 'Popular', filters: way.filters, limit: 3, sort: way.sort, excludeOwned: way.excludeOwned ?? false }
    : null
  const { data: covers } = useDiscoverFeed(request, false, true)
  const { data: count } = useDiscoverCount(request)
  return (
    <button type="button" className="discover-way" aria-pressed={active} onClick={onClick}>
      <span className="discover-way-fan" aria-hidden>
        {(covers ?? []).slice(0, 3).map((item) => {
          const src = item.thumbUrl ?? item.coverUrl
          return src ? <img key={item.providerId} src={src} alt="" loading="lazy" decoding="async" /> : null
        })}
      </span>
      <span className="discover-way-copy">
        <span className="discover-way-title">{renderLabel(way.title)}</span>
        <span className="discover-way-detail">{renderLabel(way.detail)}</span>
        {count?.count != null && (
          <span className="discover-way-count tnum">
            <Plural value={count.count} one="# title" other="# titles" />
          </span>
        )}
      </span>
    </button>
  )
}

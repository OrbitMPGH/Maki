import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { Link } from 'react-router-dom'
import {
  ActionIcon,
  Alert,
  Badge,
  Button,
  Card,
  Collapse,
  Group,
  SegmentedControl,
  Select,
  SimpleGrid,
  Skeleton,
  Stack,
  Text,
  TextInput,
} from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { notifications } from '@mantine/notifications'
import { IconAdjustmentsHorizontal, IconSearch, IconUser, IconX } from '@tabler/icons-react'
import {
  BROWSE_SORTS,
  useDiscoverFeed,
  useDiscoverSearch,
  useDiscoverSearchDefaults,
  useRootFolders,
  useSaveDiscoverSearchDefaults,
  useSeriesIdLookup,
  type BrowseSort,
  type RecommendationFilters,
  type RecommendationItem,
  type ResolvedCredit,
} from '../api/hooks'
import {
  CatalogueFilterActions,
  CatalogueFilters,
  filtersFromSpec,
  useCatalogueFilters,
} from './CatalogueFilters'
import { DiscoverDetailModal } from './DiscoverDetailModal'
import { EmptyState } from './ui/EmptyState'
import { RecommendationCard, RecommendationRow } from './ui/DiscoverRail'
import {
  POSTER_COLS_BY_DENSITY,
  ViewPrefsControls,
  readStored,
  useViewPrefs,
  writeStored,
  type ViewPrefs,
} from './ui/viewPrefs'

/**
 * Smart matches on meaning and falls back to the title index; Title is the plain FTS5 title search.
 * The stored value is the user's choice, not the engine that answered: the response says which one
 * did, and on an instance with no embedding index Smart quietly resolves to the same title search.
 */
export type SearchMode = 'smart' | 'title'

const SEARCH_MODES: readonly SearchMode[] = ['smart', 'title']

/** One page of browse results. Load more asks for this many again. */
const PAGE_SIZE = 60

/** Ceiling on how far Load more will page, matching the server's own clamp. */
const MAX_BROWSE = 600

/**
 * The catalogue, searchable and browsable, shared by Discover, Add series and the creator page.
 *
 * <p>
 * It renders browse results when the box is empty and search results when it is not, which is what
 * makes the filters mean the same thing either way: pick Romance plus Isekai and you get isekai
 * romance, then type into the box to narrow it further. The Add page relies on that, since the
 * point of opening it is often to see what exists rather than to look something up.
 * </p>
 *
 * <p>
 * Discover passes its curated rails as `idle` and keeps them; everywhere else the empty box shows
 * the filtered catalogue by popularity.
 * </p>
 */
export function CatalogueBrowser({
  scope,
  seededQuery,
  placeholder,
  idle,
  showSaveDefault = true,
  onSearchingChange,
}: {
  /** localStorage scope for the view, density and search-mode preferences. */
  scope: string
  /** Starting query, e.g. the `?q=` the command palette sends to the Add page. */
  seededQuery?: string | null
  placeholder?: string
  /** Shown instead of browse results while the box is empty. Discover passes its rails. */
  idle?: ReactNode
  showSaveDefault?: boolean
  /** Lets a host page react to the box taking over, e.g. to hide a header. */
  onSearchingChange?: (searching: boolean) => void
}) {
  const [query, setQuery] = useState(seededQuery ?? '')
  const [debounced] = useDebouncedValue(query, 400)
  const [mode, setMode] = useState<SearchMode>(() =>
    readStored(`${scope}-search-mode`, SEARCH_MODES, 'smart'),
  )
  const prefs = useViewPrefs(scope)

  // Seeded rather than controlled: the param is a starting point, and typing over it must not
  // fight the URL. Synced on change too, since arriving from the palette while already here
  // re-renders instead of remounting.
  useEffect(() => {
    if (seededQuery != null) setQuery(seededQuery)
  }, [seededQuery])

  // Title matching is useful from two characters; matching on meaning is not, and a two-character
  // query would just scan the whole index for noise.
  const minChars = mode === 'title' ? 2 : 3
  const trimmed = debounced.trim()
  const searching = trimmed.length >= minChars

  useEffect(() => {
    onSearchingChange?.(searching)
  }, [searching, onSearchingChange])

  // `applied` is separate from the live control state because a query re-runs on every change to
  // it, and dragging a slider would otherwise fire one full-catalogue query per pixel.
  const [filtersOpen, setFiltersOpen] = useState(false)
  const [applied, setApplied] = useState<RecommendationFilters>({})
  const [sort, setSort] = useState<BrowseSort>('popular')
  const [pages, setPages] = useState(1)
  const catalogue = useCatalogueFilters()

  // Seeded from the saved default exactly once, and nothing queries until that has happened:
  // searching earlier fires an unfiltered request that the hydration then immediately replaces,
  // which reads as the page flashing the wrong answer. An error hydrates too, so a failed read
  // degrades to "no default" rather than to a dead box.
  const {
    data: savedDefaults,
    isSuccess: defaultsLoaded,
    isError: defaultsFailed,
  } = useDiscoverSearchDefaults()
  const saveDefaults = useSaveDiscoverSearchDefaults()
  const [hydrated, setHydrated] = useState(false)
  const hydrateFilters = catalogue.hydrate
  useEffect(() => {
    if (hydrated) return
    if (defaultsFailed) {
      setHydrated(true)
      return
    }
    if (!defaultsLoaded || !savedDefaults) return

    const filters = filtersFromSpec(savedDefaults)
    hydrateFilters(filters)
    setApplied(filters)
    setHydrated(true)
  }, [hydrated, defaultsLoaded, defaultsFailed, savedDefaults, hydrateFilters])

  const appliedCount = Object.keys(applied).length
  const filters = appliedCount > 0 ? applied : undefined

  // A new query or a new filter set starts the browse list over.
  useEffect(() => {
    setPages(1)
  }, [applied, sort])

  const searchRequest = useMemo(
    () =>
      searching
        ? {
            query: trimmed,
            filters,
            limit: PAGE_SIZE,
            engine: mode === 'title' ? ('title' as const) : ('auto' as const),
          }
        : null,
    [searching, trimmed, filters, mode],
  )

  const browseRequest = useMemo(
    () =>
      !searching && !idle && hydrated
        ? {
            feed: 'Popular',
            filters,
            sort,
            limit: Math.min(MAX_BROWSE, PAGE_SIZE * pages),
          }
        : null,
    [searching, idle, hydrated, filters, sort, pages],
  )

  const search = useDiscoverSearch(searchRequest, hydrated, minChars)
  const browse = useDiscoverFeed(browseRequest)

  const { data: rootFolders } = useRootFolders()
  const seriesIdFor = useSeriesIdLookup()
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)

  const items = searching ? (search.data?.items ?? []) : (browse.data ?? [])
  const loading = searching ? search.isFetching && !search.data : browse.isFetching && !browse.data
  const error = searching ? search.error : browse.error
  const credits = search.data?.credits ?? []
  const corrected = search.data?.correctedQuery ?? null

  /**
   * Stores the panel as this user's default, so the next visit opens already constrained. Saves the
   * live controls rather than `applied`, matching the Recommended tab: the button says what you are
   * looking at, not what you last pressed Apply on.
   */
  const saveAsDefault = () => {
    saveDefaults.mutate(catalogue.build(), {
      onSuccess: () =>
        notifications.show({
          color: 'green',
          message: catalogue.isCustomized ? 'Saved as your default' : 'Default cleared',
        }),
      onError: (err) =>
        notifications.show({ color: 'red', message: `Failed to save default: ${String(err)}` }),
    })
  }

  const canLoadMore =
    !searching &&
    browseRequest != null &&
    items.length >= PAGE_SIZE * pages &&
    items.length < MAX_BROWSE

  return (
    <>
      <Group align="flex-start" gap="sm" mb="md" wrap="wrap">
        <TextInput
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
          placeholder={placeholder ?? 'Search by title, description, or author:"Junji Ito"'}
          leftSection={<IconSearch size={16} />}
          rightSection={
            query ? (
              <ActionIcon
                variant="subtle"
                color="gray"
                aria-label="Clear search"
                onClick={() => setQuery('')}
              >
                <IconX size={16} />
              </ActionIcon>
            ) : null
          }
          size="md"
          style={{ flex: 1, minWidth: 260 }}
        />
        <SegmentedControl
          size="md"
          value={mode}
          onChange={(v) => {
            setMode(v as SearchMode)
            writeStored(`${scope}-search-mode`, v)
          }}
          data={[
            { value: 'smart', label: 'Smart' },
            { value: 'title', label: 'Title' },
          ]}
        />
        <Button
          size="md"
          variant={appliedCount > 0 ? 'light' : 'default'}
          leftSection={<IconAdjustmentsHorizontal size={16} />}
          onClick={() => setFiltersOpen((o) => !o)}
        >
          {appliedCount > 0 ? `Filters (${appliedCount})` : 'Filters'}
        </Button>
      </Group>

      <Collapse expanded={filtersOpen}>
        <Card withBorder radius="md" padding="md" mb="md">
          <Stack gap="md">
            <CatalogueFilters controls={catalogue.controls} />
            <CatalogueFilterActions
              isCustomized={catalogue.isCustomized || appliedCount > 0}
              onReset={() => {
                catalogue.reset()
                setApplied({})
              }}
              onApply={() => setApplied(catalogue.build())}
              saving={saveDefaults.isPending}
              onSaveAsDefault={showSaveDefault ? saveAsDefault : undefined}
            />
          </Stack>
        </Card>
      </Collapse>

      {!searching && idle ? (
        idle
      ) : (
        <>
          <Group gap="xs" mb="sm" justify="space-between" wrap="wrap">
            <Group gap="xs">
              {searching ? (
                <Text c="dimmed" size="sm">
                  {items.length} match{items.length === 1 ? '' : 'es'}
                </Text>
              ) : (
                <Text c="dimmed" size="sm">
                  Browsing the catalogue
                </Text>
              )}
              {corrected && (
                <Text size="sm" c="dimmed">
                  showing results for <strong>{corrected}</strong>
                </Text>
              )}
              {credits.map((credit) => (
                <CreditChip key={`${credit.name}-${credit.roles.join()}`} credit={credit} />
              ))}
              {search.data?.mode === 'title' && mode === 'smart' && (
                <Badge variant="light" color="gray" size="sm">
                  title match only, build the recommendation index for search by meaning
                </Badge>
              )}
            </Group>
            <Group gap="xs">
              {!searching && (
                <Select
                  size="sm"
                  w={150}
                  value={sort}
                  onChange={(v) => setSort((v as BrowseSort) ?? 'popular')}
                  data={BROWSE_SORTS}
                  allowDeselect={false}
                  aria-label="Sort"
                />
              )}
              <ViewPrefsControls prefs={prefs} />
            </Group>
          </Group>

          {error && (
            <Alert color="yellow" variant="light" mb="md">
              {String(error)}
            </Alert>
          )}

          {loading && <PosterSkeletons count={12} density={prefs.density} />}

          {!loading && items.length === 0 && (
            <EmptyState
              icon={IconSearch}
              title={searching ? 'No matches' : 'Nothing here'}
              description={
                appliedCount > 0
                  ? 'Nothing matches these filters. Try loosening one of them.'
                  : searching
                    ? 'Nothing close enough. Try describing it differently, or use fewer words.'
                    : 'The catalogue needs the local MangaBaka database (Settings, then Metadata).'
              }
            />
          )}

          {items.length > 0 && <Results items={items} prefs={prefs} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />}

          {canLoadMore && (
            <Group justify="center" mt="lg">
              <Button
                variant="default"
                loading={browse.isFetching}
                onClick={() => setPages((p) => p + 1)}
              >
                Load more
              </Button>
            </Group>
          )}
        </>
      )}

      <DiscoverDetailModal
        item={detailItem}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={() => setDetailItem(null)}
      />
    </>
  )
}

/** A creator the query resolved to, linking through to everything they made. */
function CreditChip({ credit }: { credit: ResolvedCredit }) {
  return (
    <Badge
      variant="light"
      size="sm"
      leftSection={<IconUser size={11} />}
      component={Link}
      to={`/creator/${encodeURIComponent(credit.name)}`}
      style={{ cursor: 'pointer' }}
    >
      {credit.name} ({credit.workCount})
    </Badge>
  )
}

export function Results({
  items,
  prefs,
  seriesIdFor,
  onOpen,
}: {
  items: RecommendationItem[]
  prefs: ViewPrefs
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
}) {
  return prefs.viewMode === 'grid' ? (
    <SimpleGrid cols={prefs.cols} spacing="md">
      {items.map((item) => (
        <RecommendationCard
          key={item.providerId}
          item={item}
          inLibrarySeriesId={seriesIdFor(item)}
          onOpen={onOpen}
          reasonOverride={null}
        />
      ))}
    </SimpleGrid>
  ) : (
    <Stack gap="xs">
      {items.map((item) => (
        <RecommendationRow
          key={item.providerId}
          item={item}
          inLibrarySeriesId={seriesIdFor(item)}
          density={prefs.density}
          onOpen={onOpen}
          reasonOverride={null}
        />
      ))}
    </Stack>
  )
}

export function PosterSkeletons({ count, density }: { count: number; density: ViewPrefs['density'] }) {
  return (
    <SimpleGrid cols={POSTER_COLS_BY_DENSITY[density]} spacing="md">
      {Array.from({ length: count }, (_, i) => (
        <Skeleton key={i} radius="lg" style={{ aspectRatio: '2 / 3' }} />
      ))}
    </SimpleGrid>
  )
}

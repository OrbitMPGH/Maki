import { useEffect, useMemo, useState } from 'react'
import { useParams, useSearchParams } from 'react-router-dom'
import {
  Alert,
  Badge,
  Button,
  Card,
  Collapse,
  Group,
  Select,
  Stack,
  Text,
} from '@mantine/core'
import { IconAdjustmentsHorizontal, IconUser } from '@tabler/icons-react'
import {
  BROWSE_SORTS,
  useCreator,
  useRootFolders,
  useSeriesIdLookup,
  type BrowseSort,
  type RecommendationFilters,
  type RecommendationItem,
} from '../api/hooks'
import {
  CatalogueFilterActions,
  CatalogueFilters,
  useCatalogueFilters,
} from '../components/CatalogueFilters'
import { PosterSkeletons, Results } from '../components/CatalogueBrowser'
import { DiscoverDetailModal } from '../components/DiscoverDetailModal'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { useViewPrefs, ViewPrefsControls } from '../components/ui/viewPrefs'

const PAGE_SIZE = 60
const MAX_WORKS = 600

const ROLE_LABELS: Record<string, string> = {
  author: 'Story',
  artist: 'Art',
  studio: 'Studio',
}

/**
 * One author, artist or studio and everything they are credited on.
 *
 * A route rather than a modal. Junji Ito has around eighty works and Shueisha has twelve thousand,
 * which needs a grid, filters and paging; and it is reached from inside `DiscoverDetailModal`,
 * which is itself already opened from behind another modal on Discover. Navigating instead of
 * stacking keeps that at two layers, and makes the page linkable.
 */
export default function CreatorPage() {
  const { name = '' } = useParams()
  const [searchParams] = useSearchParams()
  const role = searchParams.get('role')
  // React Router already decodes path params, so this is the name as typed. Decoding it again
  // throws URIError on a name carrying a literal '%' ("100% Orange"), which blanks the page, and
  // silently rewrites one where the '%' happens to be followed by two hex digits.
  const decoded = name

  const prefs = useViewPrefs('discover')
  const catalogue = useCatalogueFilters()
  const [filtersOpen, setFiltersOpen] = useState(false)
  const [applied, setApplied] = useState<RecommendationFilters>({})
  const [sort, setSort] = useState<BrowseSort>('popular')
  const [pages, setPages] = useState(1)
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)

  // Clicking a credit on this page navigates to the same route with a different name, so React
  // Router re-renders rather than unmounting and every one of these would otherwise survive,
  // leaving the previous creator's modal open over the new page.
  useEffect(() => {
    setDetailItem(null)
    setApplied({})
    setPages(1)
    catalogue.reset()
    // catalogue.reset is stable by design; see useCatalogueFilters.
  }, [decoded, role, catalogue.reset])

  const appliedCount = Object.keys(applied).length

  const request = useMemo(
    () => ({
      name: decoded,
      role,
      filters: appliedCount > 0 ? applied : undefined,
      sort,
      limit: Math.min(MAX_WORKS, PAGE_SIZE * pages),
    }),
    [decoded, role, applied, appliedCount, sort, pages],
  )

  const { data, isFetching, error } = useCreator(decoded.length > 0 ? request : null)
  const { data: rootFolders } = useRootFolders()
  const seriesIdFor = useSeriesIdLookup()

  const items = data?.items ?? []
  const canLoadMore = items.length >= PAGE_SIZE * pages && items.length < MAX_WORKS

  if (error) {
    return (
      <>
        <PageHeader title={decoded} />
        <EmptyState
          icon={IconUser}
          title="No such creator"
          description="Nobody by that name is credited in the local MangaBaka database."
          actionLabel="Back to Discover"
          actionTo="/discover"
        />
      </>
    )
  }

  return (
    <>
      <PageHeader
        title={data?.name ?? decoded}
        description={
          data ? `${data.workCount} title${data.workCount === 1 ? '' : 's'} in the catalogue` : undefined
        }
        actions={
          <Group gap="xs">
            {(data?.roles ?? []).map((r) => (
              <Badge key={r} variant="light" size="sm">
                {ROLE_LABELS[r] ?? r}
              </Badge>
            ))}
          </Group>
        }
      />

      <Group gap="xs" mb="md" justify="space-between" wrap="wrap">
        <Button
          variant={appliedCount > 0 ? 'light' : 'default'}
          leftSection={<IconAdjustmentsHorizontal size={16} />}
          onClick={() => setFiltersOpen((o) => !o)}
        >
          {appliedCount > 0 ? `Filters (${appliedCount})` : 'Filters'}
        </Button>
        <Group gap="xs">
          <Select
            size="sm"
            w={150}
            value={sort}
            onChange={(v) => setSort((v as BrowseSort) ?? 'popular')}
            data={BROWSE_SORTS}
            allowDeselect={false}
            aria-label="Sort"
          />
          <ViewPrefsControls prefs={prefs} />
        </Group>
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
              onApply={() => {
                setApplied(catalogue.build())
                setPages(1)
              }}
            />
          </Stack>
        </Card>
      </Collapse>

      {isFetching && !data && <PosterSkeletons count={12} density={prefs.density} />}

      {data && items.length === 0 && (
        <EmptyState
          icon={IconUser}
          title="Nothing to show"
          description={
            appliedCount > 0
              ? 'None of their titles match these filters. Try loosening one of them.'
              : 'Nothing of theirs is in the searchable part of the catalogue.'
          }
        />
      )}

      {items.length > 0 && (
        <>
          <Results items={items} prefs={prefs} seriesIdFor={seriesIdFor} onOpen={setDetailItem} />
          {canLoadMore && (
            <Group justify="center" mt="lg">
              <Button variant="default" loading={isFetching} onClick={() => setPages((p) => p + 1)}>
                Load more
              </Button>
            </Group>
          )}
        </>
      )}

      {appliedCount > 0 && data && items.length > 0 && items.length < data.workCount && (
        <Alert variant="light" color="gray" mt="md">
          <Text size="sm">
            Showing {items.length} of {data.workCount} titles. Filters and the catalogue's own
            coverage both narrow this: only rated, non-novel entries are searchable.
          </Text>
        </Alert>
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

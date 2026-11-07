import { useEffect, useMemo, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import {
  Alert,
  Badge,
  Button,
  Collapse,
  Group,
  Select,
  Stack,
  Text,
} from '@mantine/core'
import { IconArrowLeft, IconBell, IconBellCheck, IconFilter } from '../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import {
  useCreator,
  useRootFolders,
  useSeriesIdLookup,
  type BrowseSort,
  type CatalogueCredit,
  type RecommendationFilters,
  type RecommendationItem,
} from '../api/hooks'
import { ApiError } from '../api/client'
import { useFollowToggle } from '../api/following'
import {
  CatalogueFilterActions,
  CatalogueFilters,
  useBrowseSortOptions,
  useCatalogueFilters,
  useLiveApply,
} from '../components/CatalogueFilters'
import { PosterSkeletons, Results } from '../components/CatalogueBrowser'
import type { ShortlistSource } from '../api/shortlist'
import { CREDIT_ROLE_LABELS } from '../components/CreditPicker'
import { HiddenContentButton, PresetMenu } from '../components/DiscoverPresets'
import { DiscoverDetailModal } from '../components/discover/DiscoverDetailModal'
import { EmptyState } from '../components/ui/EmptyState'
import { PageHeader } from '../components/ui/PageHeader'
import { Panel } from '../components/ui/Panel'
import { SurfaceFrame } from '../components/ui/SurfaceFrame'
import { TagChip } from '../components/ui/TagChip'
import { useViewPrefs, ViewPrefsControls } from '../components/ui/viewPrefs'
import { useDocumentTitle } from '../lib/documentTitle'
import { useBackTarget, usePageLabel } from '../lib/navHistory'
import { usePageState, useUnchangedSinceMount } from '../lib/pageState'
import { useLabel } from '../i18n-context'
import { ErrorMessage } from '../components/ErrorMessage'
import { FigureStrip, type Figure } from '../components/ui/FigureStrip'
import { formatFixedDecimal } from '../format'

const PAGE_SIZE = 60
// Creator links come from Discover's detail card, so a page opened cold walks back there.
const DISCOVER_FALLBACK = { to: '/discover', label: msg`Discover` }
const MAX_WORKS = 600

/**
 * One author, artist or studio and everything they are credited on.
 *
 * A route rather than a modal. Junji Ito has around eighty works and Shueisha has twelve thousand,
 * which needs a grid, filters and paging; and it is reached from inside `DiscoverDetailModal`,
 * which is itself already opened from behind another modal on Discover. Navigating instead of
 * stacking keeps that at two layers, and makes the page linkable.
 */
export default function CreatorPage() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const sortOptions = useBrowseSortOptions()
  const { name = '' } = useParams()
  const [searchParams, setSearchParams] = useSearchParams()
  const role = searchParams.get('role')
  // A follow notification links here with the new series' MangaBaka id. It may be too new to be
  // rated, and unrated series are not in the searchable part of the catalogue this page lists, so
  // it opens as the detail card rather than waiting for a card on the grid to click.
  const openId = searchParams.get('open')
  // React Router already decodes path params, so this is the name as typed. Decoding it again
  // throws URIError on a name carrying a literal '%' ("100% Orange"), which blanks the page, and
  // silently rewrites one where the '%' happens to be followed by two hex digits.
  // Trimmed once here: a whitespace-only name (`/creator/%20`) is not a real creator and every
  // other use below (scope key, request, page title) reads this same trimmed value.
  const decoded = name.trim()

  // Named for the back link on any series opened from this page.
  usePageLabel(decoded)
  useDocumentTitle(decoded)
  const back = useBackTarget(DISCOVER_FALLBACK)
  const backLink = (
    <Text component={Link} to={back.to} onClick={back.onClick} className="page-back-link" mb="sm" size="sm" fw={600}>
      <IconArrowLeft size={16} />
      {renderLabel(back.label)}
    </Text>
  )

  const prefs = useViewPrefs('discover')
  // Scoped per creator, so coming back to Junji Ito restores his filters and not the ones left on
  // the last studio looked at.
  const scope = `creator:${decoded}:${role ?? ''}`
  const catalogue = useCatalogueFilters(undefined, scope)
  const [filtersOpen, setFiltersOpen] = usePageState(`${scope}:filters-open`, false)
  const [applied, setApplied] = usePageState<RecommendationFilters>(`${scope}:applied`, {})
  const [sort, setSort] = usePageState<BrowseSort>(`${scope}:sort`, 'popular')
  const [pages, setPages] = usePageState(`${scope}:pages`, 1)
  useLiveApply(catalogue.build, setApplied)

  // New filters start the list over, but not on mount: restored filters arrive looking like a
  // change, and resetting there would drop pages loaded before opening one of the results.
  const filtersAsMounted = useUnchangedSinceMount([applied])
  useEffect(() => {
    if (filtersAsMounted) return
    setPages(1)
  }, [filtersAsMounted, applied, setPages])
  const [detailItem, setDetailItem] = useState<RecommendationItem | null>(null)
  const savedFrom = useMemo<ShortlistSource>(() => ({ savedFrom: 'creator', savedFromDetail: decoded }), [decoded])

  // Clicking a credit on this page navigates to the same route with a different name, so React
  // Router re-renders rather than unmounting and the previous creator's modal would otherwise
  // survive over the new page. The filters, sort and paging are keyed on `scope`, so
  // usePageState swaps them for the new creator's remembered ones (or the defaults) by itself.
  //
  // Only on an actual change of creator: on mount this holds whatever the last visit left.
  const sameCreator = useUnchangedSinceMount([decoded, role])
  useEffect(() => {
    if (sameCreator) return
    setDetailItem(null)
  }, [sameCreator, decoded, role])

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

  const { data, isFetching, isPlaceholderData, error, refetch } = useCreator(decoded.length > 0 ? request : null)
  // Followed under the catalogue's own spelling, and the role the page was opened for: following
  // Shueisha from its studio page must not also follow a person who happens to share the name.
  const followRole = role === 'author' || role === 'artist' || role === 'studio' ? role : null
  const followCredit: CatalogueCredit | null = data ? { name: data.name, role: followRole } : null
  const follow = useFollowToggle(followCredit)
  const { data: rootFolders } = useRootFolders()
  const seriesIdFor = useSeriesIdLookup()

  useEffect(() => {
    if (!openId || !/^\d+$/.test(openId)) return
    setDetailItem(stubItem(openId))
  }, [openId])

  const closeDetail = () => {
    setDetailItem(null)
    if (openId) {
      const next = new URLSearchParams(searchParams)
      next.delete('open')
      setSearchParams(next, { replace: true })
    }
  }

  // The header's figures need every work, not the filtered page on screen. The page's own answer
  // serves when it is unfiltered and already complete; otherwise one capped, unfiltered read.
  const pageCoversAll = appliedCount === 0 && data != null && !isPlaceholderData && data.items.length < request.limit
  const overviewRequest = useMemo(
    () => (decoded.length > 0 ? { name: decoded, role, sort: 'popular' as BrowseSort, limit: MAX_WORKS } : null),
    [decoded, role],
  )
  const { data: overview } = useCreator(pageCoversAll || data == null ? null : overviewRequest)
  const works = pageCoversAll ? data.items : overview && overview.items.length < MAX_WORKS ? overview.items : null
  const figures = useMemo(() => {
    if (!data) return []
    const list: Figure[] = [{ label: t`Titles`, value: data.workCount }]
    if (!works) return list
    list.push({ label: t`In library`, value: works.filter((w) => seriesIdFor(w) != null).length })
    const years = works.map((w) => w.year).filter((y): y is number => y != null && y > 0)
    if (years.length > 0) list.push({ label: t`Active since`, value: String(Math.min(...years)) })
    const ratings = works.map((w) => w.rating).filter((r): r is number => r != null)
    if (ratings.length > 0) {
      const mean = ratings.reduce((a, b) => a + b, 0) / ratings.length
      list.push({ label: t`Average score`, value: formatFixedDecimal(mean / 10, 1) })
    }
    return list
  }, [data, works, seriesIdFor, t])

  const items = data?.items ?? []
  const canLoadMore =
    items.length >= PAGE_SIZE * (isPlaceholderData ? pages - 1 : pages) && items.length < MAX_WORKS

  if (decoded.length === 0) {
    return (
      <SurfaceFrame width="full" pageStyle="editorial">
        {backLink}
        <PageHeader title={t`Creator`} />
        <EmptyState
          art="missing"
          title={t`Not a valid creator name`}
          description={t`This link is missing the creator's name.`}
          actionLabel={t`Back to Discover`}
          actionTo="/discover"
        />
      </SurfaceFrame>
    )
  }

  if (error) {
    const notFound = error instanceof ApiError && error.status === 404
    return (
      <SurfaceFrame width="full" pageStyle="editorial">
        {backLink}
        <PageHeader title={decoded} />
        {notFound ? (
          <EmptyState
            art="missing"
            title={t`No such creator`}
            description={t`Nobody by that name is credited in the local MangaBaka database.`}
            actionLabel={t`Back to Discover`}
            actionTo="/discover"
          />
        ) : (
          <EmptyState
            title={t`Failed to load this creator`}
            description={<ErrorMessage error={error} />}
            actionLabel={t`Retry`}
            onAction={() => void refetch()}
          />
        )}
      </SurfaceFrame>
    )
  }

  // Named for Lingui: a member access would extract as an unlabelled {0}.
  const workCount = data?.workCount ?? 0
  const shownCount = items.length

  return (
    <SurfaceFrame width="full" pageStyle="editorial">
      {backLink}
      <PageHeader
        title={data?.name ?? decoded}
        // The roles sit with the name they describe; beside Follow they read as more buttons.
        description={
          data ? (
            <span className="creator-header-meta">
              {data.roles.map((r) => (
                <TagChip key={r} size="sm">
                  {renderLabel(CREDIT_ROLE_LABELS[r] ?? r)}
                </TagChip>
              ))}
            </span>
          ) : undefined
        }
        actions={
          data && (
            <Button
              variant={follow.following ? 'light' : 'default'}
              leftSection={follow.following ? <IconBellCheck size={16} /> : <IconBell size={16} />}
              loading={follow.pending}
              disabled={!follow.ready}
              onClick={follow.toggle}
              title={
                follow.following
                  ? t`Stop showing their new titles on Home and Discover`
                  : t`Show their newest titles in a rail on Home and Discover`
              }
            >
              {follow.following ? t`Following` : t`Follow`}
            </Button>
          )
        }
      />

      {figures.length > 0 && <FigureStrip className="creator-figures" figures={figures} />}

      <Group gap="xs" mb="md" justify="space-between" wrap="wrap">
        <Button
          variant={appliedCount > 0 ? 'light' : 'default'}
          leftSection={<IconFilter size={16} />}
          rightSection={
            appliedCount > 0 ? (
              <Badge size="xs" circle variant="filled">
                {appliedCount}
              </Badge>
            ) : undefined
          }
          aria-expanded={filtersOpen}
          onClick={() => setFiltersOpen((o) => !o)}
        >
          {t`Filters`}
        </Button>
        <Group gap="xs">
          <Select
            size="sm"
            w={150}
            value={sort}
            onChange={(v) => setSort((v as BrowseSort) ?? 'popular')}
            data={sortOptions}
            allowDeselect={false}
            aria-label={t`Sort`}
          />
          <ViewPrefsControls prefs={prefs} />
        </Group>
      </Group>

      <Collapse expanded={filtersOpen}>
        <Panel p="md" mb="md">
          <Stack gap="md">
            <CatalogueFilters controls={catalogue.controls} />
            <CatalogueFilterActions
              isCustomized={catalogue.isCustomized || appliedCount > 0}
              onReset={() => {
                catalogue.reset()
                setApplied({})
              }}
              extra={
                <>
                  <PresetMenu
                    current={catalogue.build}
                    onLoad={(f) => {
                      catalogue.hydrate(f)
                      setApplied(f)
                      setPages(1)
                    }}
                  />
                  <HiddenContentButton />
                </>
              }
            />
          </Stack>
        </Panel>
      </Collapse>

      {isFetching && !data && (
        <PosterSkeletons density={prefs.density} viewMode={prefs.viewMode} />
      )}

      {data && items.length === 0 && (
        <EmptyState
          title={t`Nothing to show`}
          description={
            appliedCount > 0
              ? t`None of their titles match these filters. Try loosening one of them.`
              : t`Nothing of theirs is in the searchable part of the catalogue.`
          }
        />
      )}

      {items.length > 0 && (
        <>
          <Results items={items} prefs={prefs} seriesIdFor={seriesIdFor} onOpen={setDetailItem} savedFrom={savedFrom} />
          {canLoadMore && (
            <Group justify="center" mt="lg">
              <Button variant="default" loading={isFetching} onClick={() => setPages((p) => p + 1)}>
                <Trans>Load more</Trans>
              </Button>
            </Group>
          )}
        </>
      )}

      {appliedCount > 0 && data && items.length > 0 && items.length < data.workCount && !canLoadMore && (
        <Alert variant="light" color="var(--neutral)" mt="md">
          <Text size="sm">
            {plural(workCount, {
              one: `Showing ${shownCount} of # title. Filters and the catalogue's own coverage both narrow this: only rated, non-novel entries are searchable.`,
              other: `Showing ${shownCount} of # titles. Filters and the catalogue's own coverage both narrow this: only rated, non-novel entries are searchable.`,
            })}
          </Text>
        </Alert>
      )}

      <DiscoverDetailModal
        item={detailItem}
        inLibrarySeriesId={detailItem ? seriesIdFor(detailItem) : null}
        rootFolders={rootFolders}
        onClose={closeDetail}
        savedFrom={savedFrom}
      />
    </SurfaceFrame>
  )
}

/** Enough of a card for the detail modal, which loads everything else by id. */
function stubItem(providerId: string): RecommendationItem {
  return {
    providerId,
    title: '',
    coverUrl: null,
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

import { useMemo } from 'react'
import {
  keepPreviousData,
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query'
import { api, getInitialize, xsrfHeader } from './client'
import { useAuth } from '../auth/AuthProvider'
import type { IncognitoMode } from '../components/ui/incognito'
import type {
  AddSeriesRequest,
  ChapterDto,
  CompareSnapshot,
  MetadataLink,
  MetadataSearchResult,
  NotificationDto,
  NotificationRequest,
  LibraryFilterSpec,
  QueueHistoryDto,
  RootFolder,
  SavedFilterDto,
  SeriesDto,
  SeriesFileDto,
  SeriesScrobbleDto,
  SourceMappingDto,
  TagDto,
  UpdateSettingsDto,
  UpdateStatusDto,
} from './types'

export function useSeries() {
  return useQuery({
    queryKey: ['series'],
    queryFn: () => api<SeriesDto[]>('/series'),
  })
}

/**
 * Chapters a series still shows as missing. Uses the same denominator `CoverCard` renders
 * (`chapterCount || knownChapterCount`): `chapterCount` alone counts monitored-plus-downloaded
 * chapters, so a series whose monitored chapters are all on disk scores 0 no matter how many
 * chapters exist, and an unmonitored one scores 0 while its card reads "0/147". Sorting or
 * filtering on that made both a no-op for any library that only monitors what it already has.
 */
export function missingCount(s: SeriesDto): number {
  return (s.chapterCount || s.knownChapterCount || 0) - s.chapterFileCount
}

export interface LibraryStats {
  total: number
  monitored: number
  downloaded: number
  missing: number
  inQueue: number
  /** Null when nothing has ever reported read progress, so the tile can be hidden. */
  read: number | null
}

/**
 * Library-wide tallies, derived client-side from the series list every page already holds.
 * Shared by the Library page and the Home dashboard so the two can't quote different numbers:
 * a server-side copy would be a second implementation free to drift.
 */
export function useLibraryStats(): LibraryStats {
  const { data: series } = useSeries()
  return useMemo(() => {
    const list = series ?? []
    let downloaded = 0
    let missing = 0
    let monitored = 0
    let inQueue = 0
    let read = 0
    let tracked = false
    for (const s of list) {
      downloaded += s.chapterFileCount
      missing += Math.max(0, missingCount(s))
      if (s.monitored) monitored++
      inQueue += s.queuedCount + s.downloadingCount
      // null means "never tracked" and must not read as zero, see SeriesDto.readChapterCount.
      if (s.readChapterCount != null) {
        tracked = true
        read += s.readChapterCount
      }
    }
    return { total: list.length, monitored, downloaded, missing, inQueue, read: tracked ? read : null }
  }, [series])
}

/** Maps a catalogue (MangaBaka) item to the library series id that owns it, or null. */
export function useSeriesIdLookup() {
  const { data: library } = useSeries()
  const seriesIdByMangaBaka = useMemo(() => {
    const map = new Map<number, number>()
    for (const s of library ?? []) {
      if (s.mangaBakaId != null) map.set(s.mangaBakaId, s.id)
    }
    return map
  }, [library])
  return (item: RecommendationItem) =>
    seriesIdByMangaBaka.get(Number(item.providerId)) ?? null
}

export function useSeriesDetail(id: number) {
  return useQuery({
    queryKey: ['series', id],
    queryFn: () => api<SeriesDto>(`/series/${id}`),
    // Background source matching ends with a `sourceMatchFinished` push. A dropped hub connection
    // would otherwise leave the Sources card spinning with nothing to end it, so poll while — and
    // only while — there is something to wait for.
    refetchInterval: (query) => (query.state.data?.sourceMatchPending ? 3000 : false),
  })
}

export function useMetadataSearch(query: string) {
  return useQuery({
    queryKey: ['metadata-search', query],
    queryFn: () => api<MetadataSearchResult[]>(`/search/metadata?query=${encodeURIComponent(query)}`),
    enabled: query.trim().length > 1,
    staleTime: 5 * 60 * 1000,
  })
}

export interface RecommendationItem {
  providerId: string
  title: string
  /** Full-size cover art (~460x690). For the detail card only — poster cards use `thumbUrl`. */
  coverUrl: string | null
  /** 167x250 cover for poster cards, with `thumbUrlHiDpi` (334x500) as its 2x candidate. Null on
   *  the title-search fallback path, which has no thumbnail; fall back to `coverUrl` there. */
  thumbUrl: string | null
  thumbUrlHiDpi: string | null
  year: number | null
  description: string | null
  status: string
  rating: number | null
  totalChapters: number | null
  matchedGenres: string[]
  matchedTags: string[]
  authorMatch: boolean
  relationKind: string | null
  relatedToTitle: string | null
  becauseOfTitle: string | null
}

export interface RecommendationsResult {
  related: RecommendationItem[]
  similar: RecommendationItem[]
  generatedAt: string
  page: number
  hasMore: boolean
}

export interface RecommendationFilters {
  yearMin?: number | null
  yearMax?: number | null
  types?: string[]
  statuses?: string[]
  minRating?: number | null
  genres?: string[]
  minChapters?: number | null
  maxChapters?: number | null
  /** tags_v2 vocabulary names; all must be present on a candidate. */
  tags?: string[]
  /**
   * `ContentRating` vocabulary values to include, gated by the signed-in user's ceiling. Empty/null
   * means no constraint beyond the ceiling rails/search already apply structurally (Pornographic
   * never appears there regardless of this list).
   */
  contentRatings?: string[]
}

export interface RecommendationRequest {
  /** MangaBaka ids to base picks on. Omit/empty = the whole library. */
  seedIds?: number[]
  filters?: RecommendationFilters
  /** -1 (mainstream) … 0 (neutral) … +1 (hidden gems). */
  obscurity?: number
  /** 0 (closest matches) … 1 (spread the picks out). Drives the server's MMR re-rank. */
  diversity?: number
  refresh?: boolean
}

/**
 * What the Recommended tab picks up when the taste profile hands it a filter set. Router state, not
 * a saved default: applying a profile is a one-off look, and overwriting the user's stored default
 * to do it has no undo.
 */
export interface TasteApplyState {
  recommendationFilters: RecommendationFilters
  /** Seeds to recommend from, for "more like this group". Their titles ride along as labels. */
  seeds?: { id: number; title: string | null }[]
  source: 'taste-profile'
}

/** One of the reader's own series, as a cluster or a drift bucket shows it. */
export interface TasteMember {
  seriesId: number
  title: string
  coverUrl: string | null
}

/** A catalogue title the reader does not own, named as an example of a region. */
export interface TasteRegionTitle {
  providerId: string
  title: string
  year: number | null
}

/** A neighbourhood beside one of the reader's groups that they own nothing in. */
export interface TasteBlindSpot {
  tags: string[]
  examples: TasteRegionTitle[]
}

/** One of the distinct things a reader reads. */
export interface TasteCluster {
  /** What separates this group from the reader's OTHER groups, not from the catalogue. */
  distinctiveTags: string[]
  size: number
  share: number
  /** Mean cosine of members to the group's centre. Tight vs sprawling. */
  coherence: number
  examples: TasteMember[]
  seedIds: number[]
  blindSpot: TasteBlindSpot | null
}

export interface TasteDriftPoint {
  bucket: string
  seriesCount: number
  similarityToStart: number
  similarityToPrevious: number
  distinctiveTags: string[]
  example: TasteMember | null
}

export interface TasteInsights {
  clusters: TasteCluster[]
  /** Why the library did not divide, when it did not. Drift is still populated in that case. */
  clustersUnavailable: string | null
  oddOneOut: TasteMember | null
  oddOneOutSimilarity: number | null
  drift: TasteDriftPoint[]
  driftUnavailable: string | null
  covered: number
  total: number
  /** Why there is nothing to show. Null on success; every value is an ordinary state, not an error. */
  unavailable: string | null
  generatedAt: string
}

/**
 * What the vectors say about the caller, as opposed to what counting their genres says. Never
 * errors on a missing index or a thin library; those come back as `unavailable` with a reason.
 */
export function useTasteInsights(view: TasteView, refreshNonce = 0, enabled = true) {
  return useQuery({
    queryKey: ['taste-insights', view, refreshNonce],
    queryFn: () =>
      api<TasteInsights>(
        `/recommendations/taste-insights?view=${view}${refreshNonce > 0 ? '&refresh=true' : ''}`,
      ),
    enabled,
    staleTime: 30 * 60 * 1000,
    retry: false,
  })
}

export interface BehaviourSeries {
  seriesId: number
  title: string
  coverUrl: string | null
  /** Pre-formatted server-side, because the three lists measure different things. */
  value: string
}

/**
 * How somebody reads rather than what. Nulls mean "not enough to say", never zero: a reader with
 * no timed chapters has not read infinitely fast.
 */
export interface ReadingBehaviour {
  seriesStarted: number
  seriesFinished: number
  finishRate: number | null
  medianStopPoint: number | null
  medianSecondsPerChapter: number | null
  /** How many chapters the pace rests on. Only the native reader records time. */
  timedChapters: number
  chaptersRead: number
  readingDays: number
  medianChaptersPerReadingDay: number | null
  biggestDayCount: number | null
  biggestDay: string | null
  savoured: BehaviourSeries[]
  devoured: BehaviourSeries[]
  abandoned: BehaviourSeries[]
  generatedAt: string
}

/** Needs no catalogue, so this one answers even on an install with no MangaBaka database. */
export function useReadingBehaviour(refreshNonce = 0) {
  return useQuery({
    queryKey: ['reading-behaviour', refreshNonce],
    queryFn: () =>
      api<ReadingBehaviour>(
        `/recommendations/reading-behaviour${refreshNonce > 0 ? '?refresh=true' : ''}`,
      ),
    staleTime: 30 * 60 * 1000,
    retry: false,
  })
}

/** One thing a reader is into, and how much. */
export interface TasteFacet {
  name: string
  weight: number
  /** This facet's slice of the view's total weight, 0..1. */
  share: number
  /** Distinct series carrying it. Under the server's floor, both ratios come back null. */
  support: number
  /** Share here against the facet's flat share of the whole library. Above 1 is over-indexed. */
  overIndexShelf: number | null
  /**
   * The same against the MangaBaka catalogue, weighted toward titles more people read. Null when
   * the vector index is not built. This is catalogue popularity, not other readers' libraries.
   */
  overIndexCatalogue: number | null
}

export interface TasteYearFacet {
  year: number
  weight: number
  share: number
}

export interface TasteProfile {
  creators: TasteFacet[]
  genres: TasteFacet[]
  tags: TasteFacet[]
  types: TasteFacet[]
  years: TasteYearFacet[]
  /** Series the view was built from. The honest caveat on everything else here. */
  seriesCount: number
  libraryCount: number
  catalogueBaselineAvailable: boolean
  generatedAt: string
}

/** Which population a profile describes. Both weight a series the same way. */
export type TasteView = 'read' | 'shelf'

/**
 * The signed-in user's own taste profile. There is no user parameter: the endpoint only ever
 * answers for whoever asked.
 *
 * Pass `enabled: false` where the local MangaBaka database may be absent, for the same reason
 * `useRecommendations` does.
 */
export function useTasteProfile(view: TasteView, refreshNonce = 0, enabled = true) {
  return useQuery({
    queryKey: ['taste-profile', view, refreshNonce],
    queryFn: () =>
      api<TasteProfile>(
        `/recommendations/taste-profile?view=${view}${refreshNonce > 0 ? '&refresh=true' : ''}`,
      ),
    enabled,
    staleTime: 30 * 60 * 1000,
    retry: false,
  })
}

/**
 * Pages through the server's cached recommendation pool ("Show more" = fetchNextPage).
 *
 * Pass `enabled: false` where the local MangaBaka database may be absent: the endpoint 400s
 * without it, and with `retry: false` and no `meta.silent` that surfaces as an error toast on
 * every page load.
 */
export function useRecommendations(request: RecommendationRequest, enabled = true) {
  return useInfiniteQuery({
    queryKey: ['recommendations', request],
    queryFn: ({ pageParam }) =>
      api<RecommendationsResult>('/recommendations', {
        method: 'POST',
        // A refresh recomputes the pool: only bust the cache on the first page, so
        // deeper pages read from the pool that page 0 just rebuilt.
        body: JSON.stringify({
          ...request,
          page: pageParam,
          refresh: pageParam === 0 ? request.refresh : false,
        }),
      }),
    initialPageParam: 0,
    getNextPageParam: (last) => (last.hasMore ? last.page + 1 : undefined),
    enabled,
    staleTime: 60 * 60 * 1000,
    retry: false,
  })
}

/** One catalogue-browse rail on the Discover tab (Popular / New / Trending / …). */
export interface DiscoverRail {
  key: string
  title: string
  /** BrowseFeed name identifying the rail's source, for the "Show more" re-query. */
  feed: string
  /** Set for per-genre rails; the genre to re-query with. */
  genre: string | null
  items: RecommendationItem[]
  /** A line under the heading saying where the rail came from. Null on the catalogue rails. */
  subtitle?: string | null
  /**
   * Set only on the personalised "Based on your recent activity" rail: the MangaBaka seeds it was
   * built from. Its presence is what tells "Show more" to page the recommender instead of
   * {@link useDiscoverFeed}, whose `feed` vocabulary that rail is not part of.
   */
  seedIds?: number[] | null
}

/** Expanded ("Show more") request for a single rail: same feed, user filters, higher limit. */
export interface DiscoverFeedRequest {
  feed: string
  genre?: string | null
  filters?: RecommendationFilters
  limit?: number
  /** Rows to skip. Honoured on the in-memory path only, which is the only one that pages coherently. */
  offset?: number
  sort?: BrowseSort
}

export type BrowseSort = 'popular' | 'rating' | 'newest' | 'oldest'

export const BROWSE_SORTS: { value: BrowseSort; label: string }[] = [
  { value: 'popular', label: 'Most popular' },
  { value: 'rating', label: 'Top rated' },
  { value: 'newest', label: 'Newest' },
  { value: 'oldest', label: 'Oldest' },
]

/**
 * Catalogue-browse rails for the Discover tab (independent of the library). Bump `refreshNonce`
 * (e.g. from a Refresh button) to recompute the server-side cache; nonce 0 reads the cache.
 *
 * Pass `enabled: false` where the local MangaBaka database may be absent, see
 * {@link useRecommendations} for why that matters.
 */
export function useDiscover(refreshNonce = 0, enabled = true) {
  return useQuery({
    queryKey: ['discover-rails', refreshNonce],
    queryFn: () =>
      api<DiscoverRail[]>(`/recommendations/discover${refreshNonce > 0 ? '?refresh=true' : ''}`),
    enabled,
    staleTime: 60 * 60 * 1000,
    retry: false,
  })
}

/**
 * The one personalised Discover rail: picks seeded from the series the signed-in user read most
 * recently. Separate from {@link useDiscover} because that endpoint is cached once for the whole
 * instance and has no viewer in scope.
 *
 * Resolves to `null` when there is no reading history to seed with — an ordinary state for a new
 * account, and the caller just leaves the row out. `meta.silent` because the row is an extra on a
 * page that works without it: the local MangaBaka database being absent already raises one toast
 * from the rails query, and a second saying the same thing helps nobody.
 */
export function useDiscoverRecentActivity(refreshNonce = 0, enabled = true) {
  return useQuery({
    queryKey: ['discover-recent-activity', refreshNonce],
    queryFn: () =>
      api<DiscoverRail | null>(
        `/recommendations/discover/recent${refreshNonce > 0 ? '?refresh=true' : ''}`,
      ),
    enabled,
    staleTime: 60 * 60 * 1000,
    retry: false,
    meta: { silent: true },
  })
}

/**
 * One "Popular in {genre}" rail per genre, for the Discover Genres tab. Bump `refreshNonce` to
 * recompute the server-side cache; nonce 0 reads the cache.
 */
export function useDiscoverGenres(refreshNonce = 0) {
  return useQuery({
    queryKey: ['discover-genres', refreshNonce],
    queryFn: () =>
      api<DiscoverRail[]>(
        `/recommendations/discover/genres${refreshNonce > 0 ? '?refresh=true' : ''}`,
      ),
    staleTime: 60 * 60 * 1000,
    retry: false,
  })
}

/** Expanded, filtered view of one rail. Disabled while `request` is null (modal closed). */
export function useDiscoverFeed(request: DiscoverFeedRequest | null) {
  return useQuery({
    queryKey: ['discover-feed', request],
    queryFn: () =>
      api<RecommendationItem[]>('/recommendations/discover/feed', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
    enabled: request != null,
    staleTime: 5 * 60 * 1000,
    retry: false,
  })
}

/**
 * Which engine to ask. `auto` is the historical behaviour (meaning first, title index as a
 * fallback); `title` is the plain FTS5 title search the "Title" toggle selects. Deliberately not
 * called `mode`, because the *response* has a `mode` saying which engine actually answered.
 */
export type SearchEngine = 'auto' | 'semantic' | 'title'

/** A creator the query named, or was recognised as naming. */
export interface ResolvedCredit {
  name: string
  /** `author`, `artist`, `studio`. */
  roles: string[]
  workCount: number
}

/** Free-text Discover search: a plot description, a mood, or just a title. */
export interface DiscoverSearchRequest {
  query: string
  filters?: RecommendationFilters
  limit?: number
  engine?: SearchEngine
}

export interface DiscoverSearchResponse {
  /** `semantic` = matched on meaning; `title` = answered by the title index. */
  mode: 'semantic' | 'title'
  items: RecommendationItem[]
  /** The spelling that actually found something, when what was typed found next to nothing. */
  correctedQuery?: string | null
  credits?: ResolvedCredit[] | null
}

/**
 * Searches the catalogue. Disabled until the query has some substance: in `semantic`/`auto` a one-
 * or two-character query is noise to the embedding model and would just scan for nothing, while
 * plain title matching is useful from two characters, so the caller passes its own floor.
 */
export function useDiscoverSearch(
  request: DiscoverSearchRequest | null,
  ready = true,
  minChars = 3,
) {
  // `ready` is how the caller holds the query until its saved filter defaults have hydrated:
  // firing earlier searches unfiltered and then immediately replaces the results, which reads as
  // the page flickering to the wrong answer.
  const enabled = ready && (request?.query.trim().length ?? 0) >= minChars
  return useQuery({
    queryKey: ['discover-search', request],
    queryFn: () =>
      api<DiscoverSearchResponse>('/recommendations/discover/search', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
    enabled,
    staleTime: 5 * 60 * 1000,
    retry: false,
  })
}

/** One creator, artist or studio and the works credited to them. */
export interface CreatorRequest {
  name: string
  /** `author`, `artist`, `studio`, or omitted for any. */
  role?: string | null
  filters?: RecommendationFilters
  sort?: BrowseSort
  offset?: number
  limit?: number
}

export interface CreatorProfile {
  name: string
  roles: string[]
  /** Everything credited to them, before filters and paging. */
  workCount: number
  items: RecommendationItem[]
}

export function useCreator(request: CreatorRequest | null) {
  return useQuery({
    queryKey: ['creator', request],
    queryFn: () =>
      api<CreatorProfile>('/recommendations/creator', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
    enabled: request != null && request.name.trim().length > 0,
    staleTime: 5 * 60 * 1000,
    retry: false,
  })
}

/** Name suggestions for a partly typed creator or studio. */
export function useCreditSuggestions(query: string, role?: string | null) {
  const trimmed = query.trim()
  return useQuery({
    queryKey: ['credit-suggestions', trimmed, role ?? null],
    queryFn: () =>
      api<ResolvedCredit[]>(
        `/recommendations/credits?q=${encodeURIComponent(trimmed)}` +
          (role ? `&role=${encodeURIComponent(role)}` : ''),
      ),
    enabled: trimmed.length >= 2,
    staleTime: 5 * 60 * 1000,
    retry: false,
  })
}

/** One poster on Home's "Continue reading" or "Jump back in" rail. */
export interface HomeReadingItem {
  seriesId: number
  seriesTitle: string
  coverUrl: string | null
  chapterId: number
  /** Rendered server-side: the client holds no chapter list to resolve it from. */
  chapterLabel: string
  /** Resume position inside the chapter; 0 means start from the beginning. */
  page: number
  /** Slice length. 0 on Kavita-imported rows, which is how the resume bar knows to hide. */
  pageCount: number
  lastReadAt: string
  unreadChapters: number
}

export interface HomeReadingResponse {
  continueReading: HomeReadingItem[]
  jumpBackIn: HomeReadingItem[]
}

/** A series that recently gained chapter files. */
export interface HomeRecentSeriesItem {
  seriesId: number
  seriesTitle: string
  coverUrl: string | null
  addedAt: string
  newChapterCount: number
  newestChapterLabel: string | null
  /** Next unread downloaded chapter; null once everything downloaded has been read. */
  readChapterId: number | null
}

/**
 * Home's two reading rails.
 *
 * `refetchOnMount: 'always'` because reader position writes are fire-and-forget and invalidate
 * nothing; without it, coming back from `/read/:id` shows the resume page the rail was built with
 * rather than where you actually stopped.
 */
export function useHomeReading(limit = 12, enabled = true) {
  return useQuery({
    queryKey: ['home', 'reading', limit],
    queryFn: () => api<HomeReadingResponse>(`/home/reading?limit=${limit}`),
    enabled,
    staleTime: 30_000,
    refetchOnMount: 'always',
  })
}

/** Series that recently gained chapter files. Invalidated live by the `chapterImported` event. */
export function useHomeRecentlyAdded(limit = 12, enabled = true) {
  return useQuery({
    queryKey: ['home', 'recently-added', limit],
    queryFn: () => api<HomeRecentSeriesItem[]>(`/home/recently-added?limit=${limit}`),
    enabled,
    staleTime: 60_000,
  })
}

/** Home section keys, in the order they ship. Mirrors `HomeSections.All` on the server. */
export const HOME_SECTIONS = [
  'continue',
  'downloading',
  'recent',
  'jumpback',
  'recommended',
  'popular',
  'stats',
  'progress',
] as const

export type HomeSectionKey = (typeof HOME_SECTIONS)[number]

/** Human labels for the settings list. Home renders its own headings from its own icons. */
export const HOME_SECTION_LABELS: Record<HomeSectionKey, string> = {
  continue: 'Continue reading',
  downloading: 'Downloading now',
  recent: 'Recently added',
  jumpback: 'Jump back in',
  recommended: 'You might like',
  popular: 'Currently popular',
  stats: 'Library at a glance',
  progress: 'Your progress',
}

export interface HomeSection {
  key: HomeSectionKey
  enabled: boolean
}

export interface HomeLayout {
  /** False turns Home off entirely: no tab, no route, and "/" can't resolve there. */
  enabled: boolean
  /** Always every known key, in the user's order; the server merges before sending. */
  sections: HomeSection[]
}

/** Which supplementary rails the series page shows. Both default on. */
export interface SeriesSections {
  related: boolean
  similar: boolean
}

export interface UiSettings {
  startPage: 'home' | 'library' | 'discover'
  homeLayout: HomeLayout
  seriesSections: SeriesSections
}

/** Which page "/" resolves to, and how Home is laid out. Server-stored, so it follows the user. */
export function useUiSettings() {
  return useQuery({
    queryKey: ['settings', 'ui'],
    queryFn: () => api<UiSettings>('/settings/ui'),
    staleTime: 5 * 60 * 1000,
  })
}

export function useSaveUiSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (settings: UiSettings) =>
      api<UiSettings>('/settings/ui', { method: 'PUT', body: JSON.stringify(settings) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'ui'] })
    },
  })
}

/** Tag names for the Discover tag filter (empty until the embedding index is built). */
export function useRecommendationTags() {
  return useQuery({
    queryKey: ['recommendation-tags'],
    queryFn: () => api<string[]>('/recommendations/tags'),
    staleTime: 12 * 60 * 60 * 1000,
  })
}

/** A saved seed, with its title snapshotted so a restored seed has a label without a lookup. */
export interface RecommendationSeed {
  id: number
  title: string | null
}

/**
 * The Recommended panel as the user saved it. `minRating` is on the dump's 0–100 scale (the wire
 * filter's units), not the slider's 0–10. Never rename a field: the server reads the stored blob
 * case-insensitively and silently falls back to the default, so a rename forgets the saved panel
 * rather than erroring.
 */
export interface RecommendationDefaults {
  seeds?: RecommendationSeed[] | null
  yearMin?: number | null
  yearMax?: number | null
  types?: string[] | null
  statuses?: string[] | null
  genres?: string[] | null
  tags?: string[] | null
  minChapters?: number | null
  maxChapters?: number | null
  minRating?: number | null
  obscurity: number
  diversity: number
  contentRatings?: string[] | null
}

/** The caller's saved Recommended defaults; an all-empty spec means they have none. */
export function useRecommendationDefaults() {
  return useQuery({
    queryKey: ['recommendation-defaults'],
    queryFn: () => api<RecommendationDefaults>('/recommendations/defaults'),
    staleTime: 60 * 60 * 1000,
  })
}

/** Saves the panel as the default. An all-empty spec clears it. */
export function useSaveRecommendationDefaults() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (spec: RecommendationDefaults) =>
      api<RecommendationDefaults>('/recommendations/defaults', {
        method: 'PUT',
        body: JSON.stringify(spec),
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData(['recommendation-defaults'], saved)
    },
  })
}

/**
 * The Discover search tab's saved filter panel. The catalogue-filter half of the Recommended
 * defaults and nothing else — no seeds, no obscurity, no diversity, and a separate setting, so
 * saving one panel never rewrites the other.
 */
export interface SearchDefaults {
  yearMin?: number | null
  yearMax?: number | null
  types?: string[] | null
  statuses?: string[] | null
  genres?: string[] | null
  tags?: string[] | null
  minChapters?: number | null
  maxChapters?: number | null
  /** The dump's 0–100 scale, not the slider's 0–10. */
  minRating?: number | null
  contentRatings?: string[] | null
}

/** The caller's saved Discover-search filters; an all-empty spec means they have none. */
export function useDiscoverSearchDefaults() {
  return useQuery({
    queryKey: ['discover-search-defaults'],
    queryFn: () => api<SearchDefaults>('/recommendations/discover/searchdefaults'),
    staleTime: 60 * 60 * 1000,
  })
}

/** Saves the search filter panel as the default. An all-empty spec clears it. */
export function useSaveDiscoverSearchDefaults() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (spec: SearchDefaults) =>
      api<SearchDefaults>('/recommendations/discover/searchdefaults', {
        method: 'PUT',
        body: JSON.stringify(spec),
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData(['discover-search-defaults'], saved)
    },
  })
}

export interface MangaBakaTag {
  name: string
  weight: string
  description: string | null
  /** MangaBaka flags these as story spoilers; the UI blurs them until hover. */
  isSpoiler: boolean
}

export interface MangaBakaSourceRating {
  source: string
  rating: number
}

export interface MangaBakaDetail {
  providerId: string
  title: string
  nativeTitle: string | null
  romanizedTitle: string | null
  altTitles: string[]
  description: string | null
  coverUrl: string | null
  year: number | null
  type: string | null
  status: string
  contentRating: string | null
  rating: number | null
  sourceRatings: MangaBakaSourceRating[]
  totalChapters: number | null
  finalVolume: number | null
  authors: string[]
  artists: string[]
  publishers: string[]
  genres: string[]
  tags: MangaBakaTag[]
  links: MetadataLink[]
  malId: number | null
  hasAnime: boolean
  animeStart: number | null
  animeEnd: number | null
}

export interface MangaReview {
  author: string
  score: number | null
  text: string
  url: string | null
  date: string | null
  tags: string[]
}

/** Rich detail for a Discover recommendation. `id` is a MangaBaka id; null disables the query. */
export function useRecommendationDetail(id: string | null) {
  return useQuery({
    queryKey: ['recommendation-detail', id],
    queryFn: () => api<MangaBakaDetail>(`/recommendations/detail/${id}`),
    enabled: id != null,
    staleTime: 30 * 60 * 1000,
  })
}

/** MAL reviews for a series, fetched lazily when the detail card opens. `null` means the
 *  upstream fetch failed (Jikan/MAL outage), distinct from an empty array (fetched fine,
 *  series genuinely has none) so the UI can tell the two apart. */
export function useMangaReviews(malId: number | null) {
  return useQuery({
    queryKey: ['manga-reviews', malId],
    queryFn: () => api<MangaReview[] | null>(`/recommendations/reviews/${malId}`),
    enabled: malId != null,
    staleTime: 30 * 60 * 1000,
    retry: false,
  })
}

export interface RecommendationIndexStatus {
  modelPresent: boolean
  dumpPresent: boolean
  vectorCount: number
  recommendableTotal: number | null
  running: boolean
  phase: string
  embedded: number
  scanned: number
  startedAt: string | null
  finishedAt: string | null
  lastEmbedded: number
  lastError: string | null
  /** Seconds left at the recent throughput; null when there isn't enough to estimate yet. */
  estimatedSecondsRemaining: number | null
  /** Whether the published prebuilt index may be downloaded instead of built locally. */
  prebuiltEnabled: boolean
  /** `generatedAt` of the installed prebuilt index, or null if it was built locally. */
  prebuiltInstalledAt: string | null
  /** Active embedding model: "base" (the only selectable tier) or "off". */
  embeddingModel: string
  /** Whether the larger "full" MangaBaka dump (with MangaUpdates descriptions) is downloaded. */
  useFullDump: boolean
  /** True while a live model switch is downloading the new model + index in the background. */
  modelSwitching: boolean
  /** Why the last model switch didn't fully complete (e.g. no prebuilt index yet), or null. */
  modelSwitchError: string | null
}

export interface PrebuiltIndexResult {
  installed: boolean
  reason: string
  rowCount: number | null
}

export function useRecommendationIndex() {
  return useQuery({
    queryKey: ['recommendation-index'],
    queryFn: () => api<RecommendationIndexStatus>('/settings/recommendations'),
    // Poll quickly while an index pass or a live model switch is running; back off when idle.
    refetchInterval: (query) =>
      query.state.data?.running || query.state.data?.modelSwitching ? 2000 : false,
  })
}

export function useBuildRecommendationIndex() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () =>
      api<{ started: boolean; message?: string }>('/settings/recommendations/build', {
        method: 'POST',
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['recommendation-index'] }),
  })
}

/** Downloads the published index now (skips the freshness check, not the compatibility ones). */
export function useDownloadPrebuiltIndex() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () =>
      api<PrebuiltIndexResult>('/settings/recommendations/prebuilt/download', { method: 'POST' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['recommendation-index'] }),
  })
}

export function useSetPrebuiltIndexEnabled() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (enabled: boolean) =>
      api<{ enabled: boolean }>('/settings/recommendations/prebuilt', {
        method: 'PUT',
        body: JSON.stringify({ enabled }),
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['recommendation-index'] }),
  })
}

/** Switches the embedding model ("base"/"off") live: downloads the model + index, no restart. */
export function useSetEmbeddingModel() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (model: string) =>
      api<{ model: string; switching: boolean; reason: string }>('/settings/recommendations/model', {
        method: 'PUT',
        body: JSON.stringify({ model }),
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['recommendation-index'] }),
  })
}

/** Toggles downloading the larger "full" MangaBaka dump (local index builders only). */
export function useSetUseFullDump() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (useFullDump: boolean) =>
      api<{ useFullDump: boolean }>('/settings/recommendations/fulldump', {
        method: 'PUT',
        body: JSON.stringify({ useFullDump }),
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['recommendation-index'] }),
  })
}

export function useAddSeries() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: AddSeriesRequest) =>
      api<SeriesDto>('/series', { method: 'POST', body: JSON.stringify(request) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useDeleteSeries() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, deleteFiles }: { id: number; deleteFiles: boolean }) =>
      api<void>(`/series/${id}?deleteFiles=${deleteFiles}`, { method: 'DELETE' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useChapters(seriesId: number) {
  return useQuery({
    queryKey: ['chapters', seriesId],
    queryFn: () => api<ChapterDto[]>(`/chapter?seriesId=${seriesId}`),
  })
}

export function useSeriesFiles(seriesId: number, enabled = true) {
  return useQuery({
    queryKey: ['series-files', seriesId],
    queryFn: () => api<SeriesFileDto[]>(`/series/${seriesId}/files`),
    enabled,
  })
}

export function useSeriesScrobble(seriesId: number) {
  return useQuery({
    queryKey: ['series-scrobble', seriesId],
    queryFn: () => api<SeriesScrobbleDto>(`/series/${seriesId}/scrobble`),
  })
}

/**
 * MangaBaka relations of this series (sequels/prequels/spin-offs/side stories/main story) not
 * already in the library. Empty (never an error) when the series has no MangaBaka id or the
 * local dump isn't available; a supplementary "Related" rail, not a core feature.
 */
export function useSeriesRelated(seriesId: number, enabled = true) {
  return useQuery({
    queryKey: ['series-related', seriesId],
    queryFn: () => api<RecommendationItem[]>(`/series/${seriesId}/related`),
    enabled,
  })
}

/**
 * Series that feel like this one, for the "More like this" rail. Empty rather than an error when the
 * series has no MangaBaka id or the embedding index isn't built, so the caller just renders nothing.
 *
 * `staleTime` is an hour because the server holds its own pool for twelve: refetching on every
 * remount would only re-download the same list.
 */
export function useSeriesSimilar(seriesId: number, enabled = true) {
  return useQuery({
    queryKey: ['series-similar', seriesId],
    queryFn: () => api<RecommendationItem[]>(`/series/${seriesId}/similar`),
    enabled,
    staleTime: 60 * 60 * 1000,
  })
}

export function useRefreshSeries() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (seriesId: number) =>
      api<{ newChapters: number }>(`/series/${seriesId}/refresh`, { method: 'POST' }),
    onSuccess: (_data, seriesId) => {
      void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useRefreshMetadata() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (seriesId: number) =>
      api<SeriesDto>(`/series/${seriesId}/refreshmetadata`, { method: 'POST' }),
    onSuccess: (_data, seriesId) => {
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useMoveSeries() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({
      seriesId,
      rootFolderId,
      moveFiles = true,
    }: {
      seriesId: number
      rootFolderId: number
      moveFiles?: boolean
    }) =>
      api<SeriesDto>(`/series/${seriesId}/move`, {
        method: 'POST',
        body: JSON.stringify({ rootFolderId, moveFiles }),
      }),
    onSuccess: (_data, { seriesId }) => {
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series-files', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export interface RescanResult {
  newFiles: number
  relinked: number
  removed: number
  unrecognized: number
}

export function useRescanSeries() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (seriesId: number) =>
      api<RescanResult>(`/series/${seriesId}/rescan`, { method: 'POST' }),
    onSuccess: (_data, seriesId) => {
      void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series-files', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useSearchChapter() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (chapterId: number) =>
      api<{ queueItemId: number }>(`/chapter/${chapterId}/search`, { method: 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
    },
  })
}

export function useToggleChapterMonitor() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ chapterId, monitored }: { chapterId: number; monitored: boolean }) =>
      api<void>(`/chapter/${chapterId}/monitor?monitored=${monitored}`, { method: 'PUT' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
    },
  })
}

export function useSetChaptersMonitored() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ chapterIds, monitored }: { chapterIds: number[]; monitored: boolean }) =>
      api<{ updated: number }>('/chapter/monitor', {
        method: 'PUT',
        body: JSON.stringify({ chapterIds, monitored }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useLinkChapters() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ chapterIds, relativePath }: { chapterIds: number[]; relativePath: string }) =>
      api<{ fileId: number; linked: number }>('/chapter/link', {
        method: 'PUT',
        body: JSON.stringify({ chapterIds, relativePath }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: ['series-files'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useUnlinkChapters() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (chapterIds: number[]) =>
      api<{ unlinked: number }>('/chapter/unlink', {
        method: 'PUT',
        body: JSON.stringify(chapterIds),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: ['series-files'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useDeleteChapters() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (chapterIds: number[]) =>
      api<{ deleted: number }>('/chapter', {
        method: 'DELETE',
        body: JSON.stringify(chapterIds),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['chapters'] })
      void queryClient.invalidateQueries({ queryKey: ['series-files'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useDeleteSeriesFiles(seriesId: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (relativePaths: string[]) =>
      api<{ deleted: number; failed: number }>(`/series/${seriesId}/files`, {
        method: 'DELETE',
        body: JSON.stringify(relativePaths),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['series-files', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
    },
  })
}

/** The active queue. Paginated server-side; `total` tells you if the page is truncated. */
export function useQueue(page = 1, pageSize = 200) {
  return useQuery({
    queryKey: ['queue', page, pageSize],
    queryFn: () => api<QueueHistoryDto>(`/queue?page=${page}&pageSize=${pageSize}`),
    refetchInterval: 10_000,
  })
}

export function useQueueHistory(page: number, pageSize = 25) {
  return useQuery({
    queryKey: ['queue-history', page, pageSize],
    queryFn: () => api<QueueHistoryDto>(`/queue/history?page=${page}&pageSize=${pageSize}`),
    placeholderData: keepPreviousData,
    refetchInterval: 10_000,
  })
}

export function useRetryQueueItem() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/queue/${id}/retry`, { method: 'POST' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['queue'] }),
  })
}

export function useRemoveQueueItem() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/queue/${id}`, { method: 'DELETE' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['queue'] }),
  })
}

/** Sets the active queue's dispatch order. `orderedIds` is the full list in the desired order. */
export function useReorderQueue() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (orderedIds: number[]) =>
      api<void>('/queue/reorder', { method: 'PUT', body: JSON.stringify({ orderedIds }) }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['queue'] }),
  })
}

export function useSourceMappings(seriesId: number) {
  return useQuery({
    queryKey: ['sourcemappings', seriesId],
    queryFn: () => api<SourceMappingDto[]>(`/sourcemapping?seriesId=${seriesId}`),
  })
}

export interface MonitorModeResult {
  mode: string
  monitored: number
  total: number
}

/** Applies All / MainOnly / None to every chapter and future ones. */
export function useSetMonitorMode() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ seriesId, mode }: { seriesId: number; mode: string }) =>
      api<MonitorModeResult>(`/series/${seriesId}/monitormode`, {
        method: 'POST',
        body: JSON.stringify({ mode }),
      }),
    onSuccess: (_data, { seriesId }) => {
      void queryClient.invalidateQueries({ queryKey: ['chapters', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export interface SetIncognitoResult {
  incognito: string
}

/** "Off" | "ScrobbleOnly" | "Full" — see SeriesDto.incognito. */
export function useSetIncognito() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ seriesId, mode }: { seriesId: number; mode: string }) =>
      api<SetIncognitoResult>(`/series/${seriesId}/incognito`, {
        method: 'POST',
        body: JSON.stringify({ mode }),
      }),
    onSuccess: (_data, { seriesId }) => {
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export interface SetSeriesNotificationsResult {
  notificationMode: string
}

/** "Default" | "All" | "Reading" | "Muted" — see SeriesDto.notificationMode. */
export function useSetSeriesNotificationMode() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ seriesId, mode }: { seriesId: number; mode: string }) =>
      api<SetSeriesNotificationsResult>(`/series/${seriesId}/notifications`, {
        method: 'POST',
        body: JSON.stringify({ mode }),
      }),
    onSuccess: (_data, { seriesId }) => {
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

/**
 * Applies one notification mode across many series in one request (Library bulk bar). A real
 * endpoint rather than a loop over the per-series one: the selection can run to hundreds.
 */
export function useBulkSetSeriesNotificationMode() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ seriesIds, mode }: { seriesIds: number[]; mode: string }) =>
      api<{ updated: number }>('/series/notifications/bulk', {
        method: 'POST',
        body: JSON.stringify({ seriesIds, mode }),
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['series'] }),
  })
}

export interface SetRatingResult {
  rating: number | null
}

/**
 * Sets the user's 1–10 rating (null clears it). Returns immediately; the score push to connected
 * trackers runs in the background on the server (outcome lands in the scrobble log).
 */
export function useSetRating() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ seriesId, rating }: { seriesId: number; rating: number | null }) =>
      api<SetRatingResult>(`/series/${seriesId}/rating`, {
        method: 'PUT',
        body: JSON.stringify({ rating }),
      }),
    onSuccess: (_data, { seriesId }) => {
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['recommendations'] })
    },
  })
}

export function useTags() {
  return useQuery({
    queryKey: ['tags'],
    queryFn: () => api<TagDto[]>('/tags'),
    staleTime: 60 * 1000,
  })
}

/** Creating an existing label is a no-op server-side: it hands back the tag that's already there. */
export function useCreateTag() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ label, color }: { label: string; color?: string }) =>
      api<TagDto>('/tags', { method: 'POST', body: JSON.stringify({ label, color }) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['tags'] })
    },
  })
}

export function useUpdateTag() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, label, color }: { id: number; label?: string; color?: string }) =>
      api<TagDto>(`/tags/${id}`, { method: 'PUT', body: JSON.stringify({ label, color }) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['tags'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

export function useDeleteTag() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/tags/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['tags'] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

/** Replaces a series' tags with exactly the ids given. */
export function useSetSeriesTags() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ seriesId, tagIds }: { seriesId: number; tagIds: number[] }) =>
      api<{ tagIds: number[] }>(`/series/${seriesId}/tags`, {
        method: 'PUT',
        body: JSON.stringify({ tagIds }),
      }),
    onSuccess: (_data, { seriesId }) => {
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['tags'] })
    },
  })
}

/** Adds and/or removes tags across many series in one request (Library bulk bar). */
export function useBulkTag() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ seriesIds, add, remove }: { seriesIds: number[]; add: number[]; remove: number[] }) =>
      api<{ updated: number }>('/tags/bulk', {
        method: 'POST',
        body: JSON.stringify({ seriesIds, add, remove }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['tags'] })
    },
  })
}

export function useSavedFilters() {
  return useQuery({
    queryKey: ['library-filters'],
    queryFn: () => api<SavedFilterDto[]>('/library/filters'),
  })
}

export function useSaveFilter() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, name, spec }: { id?: number; name: string; spec: LibraryFilterSpec }) =>
      api<SavedFilterDto>(id ? `/library/filters/${id}` : '/library/filters', {
        method: id ? 'PUT' : 'POST',
        body: JSON.stringify({ name, spec }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['library-filters'] })
    },
  })
}

export function useDeleteSavedFilter() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/library/filters/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['library-filters'] })
    },
  })
}

export function useSearchMissing() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (seriesId: number) =>
      api<{ queued: number }>(`/series/${seriesId}/searchmissing`, { method: 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
    },
  })
}

export interface HealthIssue {
  type: string
  severity: string
  message: string
}

export function useHealth() {
  return useQuery({
    queryKey: ['health'],
    queryFn: () => api<HealthIssue[]>('/system/health'),
    refetchInterval: 60_000,
  })
}

/** Cached, instant: reflects the last CheckForUpdatesJob run (or a manual check-now). */
export function useUpdateStatus() {
  return useQuery({
    queryKey: ['system', 'update'],
    queryFn: () => api<UpdateStatusDto>('/system/update'),
  })
}

/**
 * The OPDS catalogue. `feedUrl` is root-relative (`/api/v1/opds/<token>`) because the server has
 * no reliable idea of the host it is reached through; the UI prefixes `window.location.origin`
 * for the copy button.
 */
export interface OpdsSettings {
  enabled: boolean
  trackProgress: boolean
  hasToken: boolean
  /** First few characters, for identifying the token. Not usable as a credential. */
  tokenPrefix: string | null
  /**
   * Set **only** on the response that generated the token. The server stores nothing but its digest,
   * so a plain GET always returns null here and the URL cannot be shown a second time.
   */
  feedUrl: string | null
}

export function useOpdsSettings() {
  return useQuery({
    queryKey: ['settings', 'opds'],
    queryFn: () => api<OpdsSettings>('/settings/opds'),
  })
}

export function useSaveOpdsSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (settings: { enabled: boolean; trackProgress: boolean }) =>
      api<OpdsSettings>('/settings/opds', { method: 'PUT', body: JSON.stringify(settings) }),
    onSuccess: (data) => {
      // The PUT mints the token on first enable, so seed the cache from the response rather
      // than refetching, otherwise the URL box stays empty until the round-trip lands.
      queryClient.setQueryData(['settings', 'opds'], data)
    },
  })
}

export function useRotateOpdsToken() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<OpdsSettings>('/settings/opds/token', { method: 'POST' }),
    onSuccess: (data) => {
      queryClient.setQueryData(['settings', 'opds'], data)
    },
  })
}

export function useUpdateSettings() {
  return useQuery({
    queryKey: ['settings', 'updates'],
    queryFn: () => api<UpdateSettingsDto>('/settings/updates'),
  })
}

export function useSaveUpdateSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (checkForUpdates: boolean) =>
      api<UpdateSettingsDto>('/settings/updates', {
        method: 'PUT',
        body: JSON.stringify({ checkForUpdates }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'updates'] })
    },
  })
}

export function useCheckForUpdatesNow() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<UpdateStatusDto>('/settings/updates/check', { method: 'POST' }),
    onSuccess: (data) => {
      queryClient.setQueryData(['system', 'update'], data)
    },
  })
}

export interface SourceInfo {
  name: string
  displayName: string
  baseUrl: string
  needsFlareSolverr: boolean
  /** Global switch. False = can't be linked, and none of its existing mappings run. */
  enabled: boolean
}

export function useSources() {
  return useQuery({
    queryKey: ['sources'],
    queryFn: () => api<SourceInfo[]>('/search/sources'),
    staleTime: Infinity,
  })
}

export interface SourceSearchResult {
  sourceSeriesId: string
  title: string
  url: string
  coverUrl: string | null
  description: string | null
}

export function useSourceSearch(sourceName: string, query: string) {
  return useQuery({
    queryKey: ['source-search', sourceName, query],
    queryFn: () =>
      api<SourceSearchResult[]>(
        `/search/source?sourceName=${encodeURIComponent(sourceName)}&query=${encodeURIComponent(query)}`,
      ),
    enabled: sourceName.length > 0 && query.trim().length > 1,
    staleTime: 5 * 60 * 1000,
  })
}

export interface ResolvedSourceUrl {
  sourceName: string
  displayName: string
  sourceSeriesId: string
  title: string
  url: string
  coverUrl: string | null
}

/** Resolves a pasted series-page URL to a source + series id. Pass '' to disable. */
export function useResolveSourceUrl(url: string) {
  return useQuery({
    queryKey: ['resolve-source', url],
    queryFn: () => api<ResolvedSourceUrl>(`/search/resolvesource?url=${encodeURIComponent(url)}`),
    enabled: url.length > 0,
    retry: false,
    staleTime: 5 * 60 * 1000,
  })
}

export function useCreateMapping() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (mapping: {
      seriesId: number
      sourceName: string
      sourceSeriesId: string
      url: string
      priority?: number
    }) => api<SourceMappingDto>('/sourcemapping', { method: 'POST', body: JSON.stringify(mapping) }),
    onSuccess: (_d, v) => {
      void queryClient.invalidateQueries({ queryKey: ['sourcemappings', v.seriesId] })
    },
  })
}

/**
 * Re-runs auto source matching for one or more series. Returns immediately with how many were
 * queued; the work itself happens in the background worker and lands via `sourceMatchFinished`.
 * Existing mappings are never touched, only sources with none are searched.
 */
export function useAutoMatchSources() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (seriesIds: number[]) =>
      api<{ queued: number }>('/sourcemapping/automatch', {
        method: 'POST',
        body: JSON.stringify({ seriesIds }),
      }),
    // Prefix match: picks up ['series', id], whose pending flag drives the spinner and the poll.
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['series'] }),
  })
}

export function useUpdateMapping() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (mapping: SourceMappingDto) =>
      api<SourceMappingDto>(`/sourcemapping/${mapping.id}`, {
        method: 'PUT',
        body: JSON.stringify(mapping),
      }),
    onSuccess: (_d, v) => {
      void queryClient.invalidateQueries({ queryKey: ['sourcemappings', v.seriesId] })
    },
  })
}

/**
 * Rewrites a series' whole source order in one call, most preferred first. Dragging columns changes
 * every rank at once, so doing it through `useUpdateMapping` would fire one request per source.
 */
export function useReorderMappings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: { seriesId: number; orderedMappingIds: number[] }) =>
      api<SourceMappingDto[]>('/sourcemapping/priority', {
        method: 'PUT',
        body: JSON.stringify(value),
      }),
    onSuccess: (_d, v) => {
      void queryClient.invalidateQueries({ queryKey: ['sourcemappings', v.seriesId] })
    },
  })
}

/**
 * Kicks off a source comparison. The response is the initial snapshot, seeded into the query cache
 * so the modal has panels to draw before the first poll comes back.
 */
export function useStartSourceCompare() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: { seriesId: number; chapterNumber?: number }) =>
      api<CompareSnapshot>('/sourcemapping/compare', {
        method: 'POST',
        body: JSON.stringify(value),
      }),
    onSuccess: (snapshot, v) => {
      queryClient.setQueryData(['source-compare', v.seriesId], snapshot)
    },
  })
}

/**
 * Polls a running comparison. Sources fetch in parallel and land independently, so this keeps
 * ticking until the backend reports every panel settled.
 */
export function useSourceCompare(seriesId: number, enabled: boolean) {
  return useQuery({
    queryKey: ['source-compare', seriesId],
    queryFn: () => api<CompareSnapshot>(`/sourcemapping/compare?seriesId=${seriesId}`),
    enabled,
    retry: false,
    refetchInterval: (query) => (query.state.data?.running ? 1500 : false),
  })
}

/**
 * Re-downloads this series' chapters that came from any source other than `sourceName`. Chapters
 * the preferred source doesn't list come back in `unavailable` rather than being re-fetched from
 * the source they already came from, and files imported from disk are never touched.
 */
export function useRedownloadFromSource() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: { seriesId: number; sourceName: string }) =>
      api<{ queued: number; unavailable: number }>('/chapter/redownload', {
        method: 'POST',
        body: JSON.stringify(value),
      }),
    onSuccess: (_d, v) => {
      void queryClient.invalidateQueries({ queryKey: ['chapters', v.seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
    },
  })
}

export function useDeleteMapping() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id }: { id: number; seriesId: number }) =>
      api<void>(`/sourcemapping/${id}`, { method: 'DELETE' }),
    onSuccess: (_d, v) => {
      void queryClient.invalidateQueries({ queryKey: ['sourcemappings', v.seriesId] })
    },
  })
}

export function useFlareSolverrSettings() {
  return useQuery({
    queryKey: ['settings', 'flaresolverr'],
    queryFn: () => api<{ url: string | null }>('/settings/flaresolverr'),
  })
}

export function useSaveFlareSolverr() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (url: string | null) =>
      api<{ url: string | null }>('/settings/flaresolverr', {
        method: 'PUT',
        body: JSON.stringify({ url }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'flaresolverr'] })
    },
  })
}

export function useTestFlareSolverr() {
  return useMutation({
    mutationFn: (url: string | null) =>
      api<{ success: boolean }>('/settings/flaresolverr/test', {
        method: 'POST',
        body: JSON.stringify({ url }),
      }),
  })
}

export interface ProwlarrSettings {
  url: string | null
  apiKey: string | null
}

export interface QBittorrentSettings {
  url: string | null
  username: string | null
  password: string | null
  category: string | null
}

export function useConnectionSettings<T>(name: 'prowlarr' | 'qbittorrent' | 'kavita') {
  return useQuery({
    queryKey: ['settings', name],
    queryFn: () => api<T>(`/settings/${name}`),
  })
}

export function useSaveConnectionSettings<T>(name: 'prowlarr' | 'qbittorrent' | 'kavita') {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: T) =>
      api<T>(`/settings/${name}`, { method: 'PUT', body: JSON.stringify(value) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', name] })
    },
  })
}

export function useTestConnectionSettings<T>(name: 'prowlarr' | 'qbittorrent' | 'kavita') {
  return useMutation({
    mutationFn: (value: T) =>
      api<{ success: boolean }>(`/settings/${name}/test`, {
        method: 'POST',
        body: JSON.stringify(value),
      }),
  })
}

export interface ProwlarrOptions {
  indexerIds: string | null
  categories: string | null
}

export interface ProwlarrIndexer {
  id: number
  name: string
  enable: boolean
  protocol: string | null
  categories: { id: number; name: string }[]
}

export function useProwlarrOptions() {
  return useQuery({
    queryKey: ['settings', 'prowlarr-options'],
    queryFn: () => api<ProwlarrOptions>('/settings/prowlarr/options'),
  })
}

export function useSaveProwlarrOptions() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: ProwlarrOptions) =>
      api<ProwlarrOptions>('/settings/prowlarr/options', { method: 'PUT', body: JSON.stringify(value) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'prowlarr-options'] })
    },
  })
}

export function useProwlarrIndexers(enabled: boolean) {
  return useQuery({
    queryKey: ['prowlarr-indexers'],
    queryFn: () => api<ProwlarrIndexer[]>('/settings/prowlarr/indexers'),
    enabled,
    retry: false,
    staleTime: 5 * 60 * 1000,
  })
}

export interface ReleaseDto {
  guid: string
  title: string
  size: number
  indexer: string
  seeders: number | null
  leechers: number | null
  protocol: string
  downloadUrl: string | null
  magnetUrl: string | null
  infoUrl: string | null
}

export interface ReleaseSearchResult {
  query: string
  releases: ReleaseDto[]
}

export function useReleaseSearch(seriesId: number, enabled: boolean, query?: string) {
  return useQuery({
    queryKey: ['releases', seriesId, query ?? ''],
    queryFn: () =>
      api<ReleaseSearchResult>(
        `/release?seriesId=${seriesId}${query ? `&query=${encodeURIComponent(query)}` : ''}`,
      ),
    enabled,
    staleTime: 5 * 60 * 1000,
    retry: false,
  })
}

export function useGrabRelease() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (payload: { seriesId: number; release: ReleaseDto }) =>
      api<{ queueItemId: number }>('/release/grab', {
        method: 'POST',
        body: JSON.stringify(payload),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
    },
  })
}

export interface MetadataSettings {
  useLocalDb: boolean
  dumpPresent: boolean
  dumpSizeBytes: number | null
  dumpRefreshedAt: string | null
}

export function useMetadataSettings() {
  return useQuery({
    queryKey: ['settings', 'metadata'],
    queryFn: () => api<MetadataSettings>('/settings/metadata'),
  })
}

export function useSaveMetadataSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (useLocalDb: boolean) =>
      api<MetadataSettings>('/settings/metadata', {
        method: 'PUT',
        body: JSON.stringify({ useLocalDb }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'metadata'] })
    },
  })
}

export interface MonitoringSettings {
  unmonitorSpecials: boolean
}

export function useMonitoringSettings() {
  return useQuery({
    queryKey: ['settings', 'monitoring'],
    queryFn: () => api<MonitoringSettings>('/settings/monitoring'),
  })
}

export function useSaveMonitoringSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (unmonitorSpecials: boolean) =>
      api<MonitoringSettings>('/settings/monitoring', {
        method: 'PUT',
        body: JSON.stringify({ unmonitorSpecials }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'monitoring'] })
    },
  })
}

export type ContentRating = 'safe' | 'suggestive' | 'erotica' | 'pornographic'

/** Least to most explicit, mirroring the server's `ContentRating.All` order. */
export const CONTENT_RATINGS: ContentRating[] = ['safe', 'suggestive', 'erotica', 'pornographic']

/** Ratings at or below `max` — what a content-rating filter should offer as options. */
export function allowedContentRatings(max: ContentRating | string | undefined | null): ContentRating[] {
  const index = CONTENT_RATINGS.indexOf(max as ContentRating)
  return CONTENT_RATINGS.slice(0, index < 0 ? 1 : index + 1)
}

export const CONTENT_RATING_LABELS: Record<string, string> = {
  safe: 'Safe',
  suggestive: 'Suggestive',
  erotica: 'Erotica',
  pornographic: 'Pornographic',
}

export interface DiscoverSettings {
  maxContentRating: ContentRating
}

export function useDiscoverSettings() {
  return useQuery({
    queryKey: ['settings', 'discover'],
    queryFn: () => api<DiscoverSettings>('/settings/discover'),
  })
}

export function useSaveDiscoverSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (maxContentRating: ContentRating) =>
      api<DiscoverSettings>('/settings/discover', {
        method: 'PUT',
        body: JSON.stringify({ maxContentRating }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'discover'] })
    },
  })
}

export type FolderNamingMode = 'rename' | 'keep-new-standard' | 'keep-original'

export interface LibrarySettings {
  writeComicInfo: boolean
  folderNamingMode: FolderNamingMode
  /**
   * Content rating ("safe" | "suggestive" | "erotica" | "pornographic") → the incognito mode a
   * newly added series of that rating starts at. Always complete on read. Leave it out of a write
   * to keep the stored rules as they are.
   */
  incognitoByRating?: Record<string, IncognitoMode>
  /** Also copy the downloaded poster into the series' library folder as "cover.jpg", for other
   * tools (Komga, Kavita) that read a cover placed directly in the folder. Default off. */
  writeCoverToFolder?: boolean
  /**
   * Naming format for a series' folder, e.g. "{Series TitleYear}". Always filled in on read.
   * Leave it out of a write to keep the stored format — same contract as incognitoByRating, and
   * the reason the setup wizard's partial saves don't blank it.
   */
  seriesFolderFormat?: string
  /** Naming format for a downloaded chapter's file, extension excluded. */
  chapterFormat?: string
}

export function useLibrarySettings() {
  return useQuery({
    queryKey: ['settings', 'library'],
    queryFn: () => api<LibrarySettings>('/settings/library'),
  })
}

export function useSaveLibrarySettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (settings: LibrarySettings) =>
      api<LibrarySettings>('/settings/library', {
        method: 'PUT',
        body: JSON.stringify(settings),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'library'] })
    },
  })
}

export interface NamingToken {
  token: string
  category: string
  description: string
  example: string
}

export interface NamingPreview {
  seriesFolder: string
  chapterFile: string
  errors: string[]
}

/** The token catalogue for the picker. Static per release, so it's cached for the session. */
export function useNamingTokens() {
  return useQuery({
    queryKey: ['settings', 'naming', 'tokens'],
    queryFn: () => api<NamingToken[]>('/settings/naming/tokens'),
    staleTime: Infinity,
  })
}

/**
 * Renders both formats against the server's sample. Server-side on purpose: the example an admin
 * approves and the name that lands on disk come out of one implementation.
 */
export function useNamingPreview(seriesFolderFormat: string, chapterFormat: string) {
  return useQuery({
    queryKey: ['settings', 'naming', 'preview', seriesFolderFormat, chapterFormat],
    queryFn: () =>
      api<NamingPreview>('/settings/naming/preview', {
        method: 'POST',
        body: JSON.stringify({ seriesFolderFormat, chapterFormat }),
      }),
    enabled: seriesFolderFormat.length > 0 && chapterFormat.length > 0,
    placeholderData: (previous) => previous,
  })
}

export interface SeriesRenameFile {
  chapterFileId: number
  from: string
  to: string
}

export interface SeriesRenamePlan {
  seriesId: number
  title: string
  folderFrom: string
  folderTo: string
  files: SeriesRenameFile[]
  conflicts: string[]
  folderChanged: boolean
  hasChanges: boolean
}

export interface SeriesRenameResult {
  plan: SeriesRenamePlan | null
  applied: boolean
  error: string | null
  warnings: string[]
}

/** What renaming this series to the current formats would move. Read-only. */
export function useSeriesRenamePreview(seriesId: number, enabled: boolean) {
  return useQuery({
    queryKey: ['series', seriesId, 'rename-preview'],
    queryFn: () => api<SeriesRenamePlan>(`/series/${seriesId}/rename/preview`),
    enabled,
  })
}

export function useRenameSeries(seriesId: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () =>
      api<SeriesRenameResult>(`/series/${seriesId}/rename`, { method: 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId, 'rename-preview'] })
    },
  })
}

export interface SetupStatus {
  completed: boolean
}

export function useSetupStatus() {
  return useQuery({
    queryKey: ['settings', 'setup'],
    queryFn: () => api<SetupStatus>('/settings/setup'),
    staleTime: Infinity,
  })
}

export function useCompleteSetup() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (completed: boolean) =>
      api<SetupStatus>('/settings/setup', {
        method: 'PUT',
        body: JSON.stringify({ completed }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'setup'] })
    },
  })
}

export interface DownloadSettings {
  concurrentChapters: number
  retryEnabled: boolean
  retryMaxAttempts: number
  smartDownloadChaptersLeft : number
  smartDownloadChapters : number
  /** Wall-clock cap on one chapter download before the worker gives up on it. 0 means no cap. */
  itemTimeoutMinutes: number
}

export function useDownloadSettings() {
  return useQuery({
    queryKey: ['settings', 'download'],
    queryFn: () => api<DownloadSettings>('/settings/download'),
  })
}

export function useSaveDownloadSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: DownloadSettings) =>
      api<DownloadSettings>('/settings/download', {
        method: 'PUT',
        body: JSON.stringify(value),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'download'] })
    },
  })
}

export interface SourcePrioritySettings {
  order: string[]
  /** Globally switched-off sources. They stay in `order` so an off/on cycle keeps their rank. */
  disabled: string[]
}

/** Admin-only endpoint, so callers outside Settings have to gate this on the caller being one. */
export function useSourcePriority(enabled = true) {
  return useQuery({
    queryKey: ['settings', 'sources', 'priority'],
    queryFn: () => api<SourcePrioritySettings>('/settings/sources/priority'),
    enabled,
  })
}

export function useSaveSourcePriority() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: SourcePrioritySettings) =>
      api<SourcePrioritySettings>('/settings/sources/priority', {
        method: 'PUT',
        body: JSON.stringify(value),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'sources', 'priority'] })
      // /search/sources carries the enabled flag and is cached with staleTime: Infinity,
      // so every screen showing source state would go stale without this.
      void queryClient.invalidateQueries({ queryKey: ['sources'] })
    },
  })
}

export function useRefreshMetadataDump() {
  return useMutation({
    mutationFn: () =>
      api<{ started: boolean }>('/settings/metadata/refresh', { method: 'POST' }),
  })
}

export function useGeneralSettings() {
  return useQuery({
    queryKey: ['settings', 'general'],
    // No apiKey any more: there is no instance-wide key. Credentials belong to accounts and are
    // managed under Settings → My account.
    queryFn: () => api<{ port: number }>('/settings/general'),
  })
}

/**
 * Root folders, or nothing at all for a non-admin.
 *
 * `GET /rootfolder` is admin-only on purpose: a root folder is a filesystem path on the host and
 * listing them discloses its directory layout. Without the gate every non-admin landing on the
 * library, Home, Discover, a series page or the request form fires a request that 403s, and the
 * global query-error handler turns each one into a red toast on page load. Every call site already
 * treats the list as optional.
 */
export function useRootFolders() {
  const { can } = useAuth()
  return useQuery({
    queryKey: ['rootfolders'],
    queryFn: () => api<RootFolder[]>('/rootfolder'),
    enabled: can('Admin'),
  })
}

export function useAddRootFolder() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (path: string) =>
      api<RootFolder>('/rootfolder', { method: 'POST', body: JSON.stringify({ path }) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['rootfolders'] })
    },
  })
}

export function useDeleteRootFolder() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/rootfolder/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['rootfolders'] })
    },
  })
}

// ---- Scrobbling ----

export interface ScrobbleConnection {
  service: string
  label: string
  configured: boolean
  connected: boolean
  username: string | null
  oAuth: boolean
  /** Per-tracker: push reading progress to this service. */
  syncReading: boolean
  /** Per-tracker: push ratings to this service. */
  syncRatings: boolean
}

export interface ScrobbleCandidate {
  id: string
  title: string
  url: string
}

export interface ScrobbleUnmatchedItem {
  kavitaSeriesId: number
  service: string
  title: string
  reason: string
  candidates: ScrobbleCandidate[]
}

export interface ScrobbleSyncRow {
  title: string
  service: string
  chapter: number
  volume: number
  status: string | null
  at: string
  error: string | null
}

export interface ScrobbleLogRow {
  timestamp: string
  level: string
  service: string
  title: string
  message: string
}

export interface ScrobbleStatus {
  connections: ScrobbleConnection[]
  running: boolean
  lastSyncAt: string | null
  nextSyncAt: string | null
  intervalMinutes: number
  planToRead: boolean
  recent: ScrobbleSyncRow[]
  unmatched: ScrobbleUnmatchedItem[]
  log: ScrobbleLogRow[]
}

export function useAppVersion() {
  return useQuery({
    queryKey: ['app-version'],
    queryFn: async () => (await getInitialize()).version,
    staleTime: Infinity,
  })
}

export function useScrobbleStatus() {
  return useQuery({
    queryKey: ['scrobble', 'status'],
    queryFn: () => api<ScrobbleStatus>('/scrobble/status'),
    refetchInterval: 5000,
  })
}

export function useScrobbleSyncNow() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<{ message: string }>('/scrobble/sync', { method: 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scrobble'] })
      void queryClient.invalidateQueries({ queryKey: ['series-scrobble'] })
    },
  })
}

export function useScrobbleMatch() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: { kavitaSeriesId: number; service: string; remoteId: string }) =>
      api<{ message: string }>('/scrobble/match', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scrobble'] })
      void queryClient.invalidateQueries({ queryKey: ['series-scrobble'] })
    },
  })
}

export function useScrobbleIgnore() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (request: { kavitaSeriesId: number; service: string }) =>
      api<{ message: string }>('/scrobble/ignore', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scrobble'] })
      void queryClient.invalidateQueries({ queryKey: ['series-scrobble'] })
    },
  })
}

export function useScrobbleAuthStart() {
  return useMutation({
    // Pass the origin the user is actually browsing so the OAuth redirect URI lands
    // back on this SPA, not the API host, which can differ (dev: SPA :5173 / API :8990).
    mutationFn: (service: string) =>
      api<{ url: string }>(
        `/scrobble/auth/${service}/start?origin=${encodeURIComponent(window.location.origin)}`,
      ),
  })
}

export function useScrobbleDisconnect() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (service: string) =>
      api<{ message: string }>(`/scrobble/auth/${service}/disconnect`, { method: 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scrobble'] })
    },
  })
}

/** Sets the per-tracker "scrobble reading" / "sync ratings" toggles. */
export function useScrobblePreferences() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({
      service,
      reading,
      ratings,
    }: {
      service: string
      reading: boolean
      ratings: boolean
    }) =>
      api<{ service: string; reading: boolean; ratings: boolean }>(
        `/scrobble/preferences/${service}`,
        { method: 'PUT', body: JSON.stringify({ reading, ratings }) },
      ),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['scrobble', 'status'] })
    },
  })
}

export interface RatingImportItem {
  seriesId: number
  title: string
  localRating: number | null
  remoteScore: number
}

export interface RatingImportState {
  running: boolean
  computedAt: string | null
  error: string | null
  items: RatingImportItem[]
}

/** Kicks off a background preview of the ratings held on a service. */
export function useStartRatingImport() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (service: string) =>
      api<{ started: boolean }>(`/scrobble/import-ratings/${service}/preview`, { method: 'POST' }),
    onSuccess: (_data, service) => {
      void queryClient.invalidateQueries({ queryKey: ['rating-import', service] })
    },
  })
}

/** Polls the in-flight/last rating-import preview for a service. Only enabled while a modal is open. */
export function useRatingImport(service: string, enabled: boolean) {
  return useQuery({
    queryKey: ['rating-import', service],
    queryFn: () => api<RatingImportState>(`/scrobble/import-ratings/${service}`),
    enabled,
    refetchInterval: (query) => (query.state.data?.running ? 1500 : false),
  })
}

/** Applies the chosen previewed remote scores to local ratings. */
export function useApplyRatingImport() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ service, seriesIds }: { service: string; seriesIds: number[] }) =>
      api<{ applied: number }>(`/scrobble/import-ratings/${service}/apply`, {
        method: 'POST',
        body: JSON.stringify({ seriesIds }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['recommendations'] })
    },
  })
}

export interface ScrobbleSettings {
  aniListClientId: string | null
  aniListClientSecret: string | null
  malClientId: string | null
  malClientSecret: string | null
  mangaBakaToken: string | null
  kitsuClientId: string | null
  kitsuClientSecret: string | null
  kitsuEmail: string | null
  kitsuPassword: string | null
  intervalMinutes: number
  planToRead: boolean
  libraryIds: string | null
}

export function useScrobbleSettings() {
  return useQuery({
    queryKey: ['settings', 'scrobble'],
    queryFn: () => api<ScrobbleSettings>('/settings/scrobble'),
  })
}

export function useSaveScrobbleSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: ScrobbleSettings) =>
      api<ScrobbleSettings>('/settings/scrobble', { method: 'PUT', body: JSON.stringify(value) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'scrobble'] })
      void queryClient.invalidateQueries({ queryKey: ['scrobble'] })
    },
  })
}

// ---- Backups ---------------------------------------------------------------

export interface BackupManifest {
  appVersion: string
  createdUtc: string
  lastMigration: string | null
  kind: string
}

export interface BackupInfo {
  name: string
  sizeBytes: number
  manifest: BackupManifest
}

export function useBackups() {
  return useQuery({
    queryKey: ['backups'],
    queryFn: () => api<BackupInfo[]>('/system/backups'),
  })
}

export function useCreateBackup() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<BackupInfo>('/system/backups', { method: 'POST' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['backups'] }),
  })
}

export function useDeleteBackup() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) =>
      api<void>(`/system/backups/${encodeURIComponent(name)}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['backups'] }),
  })
}

export function useRestoreBackup() {
  return useMutation({
    mutationFn: (name: string) =>
      api<{ message: string }>(`/system/backups/${encodeURIComponent(name)}/restore`, {
        method: 'POST',
      }),
  })
}

// Download and upload bypass the shared api() helper: it forces Content-Type: application/json
// and JSON-parses the body, both wrong for a zip blob / multipart form. The session cookie still
// authenticates them, and the upload still needs the antiforgery header.
export async function downloadBackup(name: string): Promise<void> {
  const init = await getInitialize()
  const res = await fetch(`${init.apiRoot}/system/backups/${encodeURIComponent(name)}`, {
    credentials: 'same-origin',
  })
  if (!res.ok) throw new Error(`Download failed: ${res.status}`)
  const blob = await res.blob()
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  URL.revokeObjectURL(url)
}

export function useUploadRestore() {
  return useMutation({
    mutationFn: async (file: File) => {
      const init = await getInitialize()
      const form = new FormData()
      form.append('file', file)
      const res = await fetch(`${init.apiRoot}/system/backups/restore-upload`, {
        method: 'POST',
        credentials: 'same-origin',
        // Not authHeaders(): that sets a JSON Content-Type, which would stop the browser writing the
        // multipart boundary. Only the antiforgery token is wanted here.
        headers: xsrfHeader(),
        body: form,
      })
      if (!res.ok) {
        const body = await res.text()
        throw new Error(body || `Upload failed: ${res.status}`)
      }
      return (await res.json()) as { message: string }
    },
  })
}

export interface BackupRetentionSettings {
  retention: number
}

export function useBackupSettings() {
  return useQuery({
    queryKey: ['settings', 'backup'],
    queryFn: () => api<BackupRetentionSettings>('/settings/backup'),
  })
}

export function useSaveBackupSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: BackupRetentionSettings) =>
      api<BackupRetentionSettings>('/settings/backup', {
        method: 'PUT',
        body: JSON.stringify(value),
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['settings', 'backup'] }),
  })
}

// ---- Image cache -----------------------------------------------------------

export interface ImageCacheStatus {
  running: boolean
  /** "idle", "clearing" (thumbnails) or "covers". */
  phase: string
  /** Whether the run re-downloads every poster or only the missing ones. */
  force: boolean
  processed: number
  total: number
  downloaded: number
  failed: number
  /** Series with no provider id, so there is no poster to fetch. */
  skipped: number
  thumbnailsCleared: number
  startedAt: string | null
  finishedAt: string | null
  lastError: string | null
}

export interface ImageCacheUsage {
  coverFiles: number
  coverBytes: number
  thumbnailFiles: number
  thumbnailBytes: number
  seriesTotal: number
  coversMissing: number
}

export interface ImageCacheInfo {
  status: ImageCacheStatus
  usage: ImageCacheUsage
}

/**
 * @param awaitingStart keeps polling over the gap between the trigger returning and the job
 * actually claiming the run. Without it the first refetch after the click reads `running: false`,
 * polling never starts, and the card sits on the previous run's summary until the page is
 * revisited.
 */
export function useImageCache(awaitingStart = false) {
  return useQuery({
    queryKey: ['image-cache'],
    queryFn: () => api<ImageCacheInfo>('/system/image-cache'),
    // Poll while a rebuild runs; idle costs a walk of the thumbnail folder, so don't poll then.
    refetchInterval: (query) => (query.state.data?.status.running || awaitingStart ? 1500 : false),
  })
}

export function useRebuildImageCache() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (force: boolean) =>
      api<{ started: boolean; message?: string }>('/system/image-cache/rebuild', {
        method: 'POST',
        body: JSON.stringify({ force }),
      }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['image-cache'] }),
  })
}

// ---- Reading activity ------------------------------------------------------
// One window of a reader's activity. Feeds the Stats page's Overview tab, and the Rewind
// slideshow off the same payload — "Rewind" is a consumer of this, not a shape of its own.

/** userId targets another account; admin-only server-side, and ignored for anyone else. */
const forUser = (userId?: number) => (userId ? `?userId=${userId}` : '')

export interface ActivityTotals {
  chaptersRead: number
  volumesRead: number
  chaptersDownloaded: number
  seriesAdded: number
  seriesRemoved: number
  seriesFinished: number
  seriesDropped: number
  /**
   * Active seconds in Maki's own reader. Kavita reports what was read but never for how long, so
   * this is legitimately 0 for somebody whose reading all arrives over the Kavita pass.
   */
  readingSeconds: number
  /** Distinct local dates in the window on which anything was read. */
  daysActive: number
}

/** bucket is "yyyy-MM" (month granularity) or "yyyy-MM-dd" (ranges ≤ 62 days). */
export interface ActivityTimelinePoint {
  bucket: string
  chaptersRead: number
  chaptersDownloaded: number
  seriesAdded: number
  readingSeconds: number
}

/** coverUrl is null for a series that has since been removed, or one outside your root folders. */
export interface ActivitySeriesStat {
  seriesId: number | null
  title: string
  count: number
  coverUrl: string | null
}

export interface ActivitySeriesTime {
  seriesId: number | null
  title: string
  seconds: number
  coverUrl: string | null
}

export interface ActivityWeightedName {
  name: string
  weight: number
}

export interface ActivitySeriesEvent {
  seriesId: number | null
  title: string
  at: string
  coverUrl: string | null
}

export interface ActivityDroppedSeries {
  seriesId: number | null
  title: string
  lastProgressAt: string
  maxChapter: number
  coverUrl: string | null
}

export interface ActivityStats {
  from: string
  to: string
  readTrackingAvailable: boolean
  totals: ActivityTotals
  timeline: ActivityTimelinePoint[]
  topRead: ActivitySeriesStat[]
  leastRead: ActivitySeriesStat[]
  topGenres: ActivityWeightedName[]
  topTags: ActivityWeightedName[]
  finished: ActivitySeriesEvent[]
  added: ActivitySeriesEvent[]
  removed: ActivitySeriesEvent[]
  dropped: ActivityDroppedSeries[]
  topByTime: ActivitySeriesTime[]
}

export function useActivityYears(userId?: number) {
  return useQuery({
    queryKey: ['stats', 'years', userId ?? 'me'],
    queryFn: () => api<number[]>(`/stats/years${forUser(userId)}`),
  })
}

/**
 * from/to are inclusive local dates (yyyy-MM-dd); the browser's UTC offset is sent along so
 * day/month buckets match the user's calendar.
 *
 * `userId` is part of the query key, not just the URL: without it an admin switching readers gets
 * a cache hit on their own numbers under somebody else's name.
 */
export function useActivityStats(from: string, to: string, userId?: number, enabled = true) {
  return useQuery({
    queryKey: ['stats', 'activity', from, to, userId ?? 'me'],
    queryFn: () =>
      api<ActivityStats>(
        `/stats/activity?from=${from}&to=${to}&utcOffsetMinutes=${new Date().getTimezoneOffset()}` +
          (userId ? `&userId=${userId}` : ''),
      ),
    enabled,
    placeholderData: keepPreviousData,
  })
}

// ---- Library composition ---------------------------------------------------
// Distinct from `useLibraryStats` above, which tallies the series list the client already holds.
// This is the server-side view: sizes, sources, growth — things no page has in memory.

export interface LibraryCompositionTotals {
  seriesCount: number
  monitoredCount: number
  completedCount: number
  chapterCount: number
  downloadedChapterCount: number
  fileCount: number
  totalBytes: number
}

export interface NamedCount {
  name: string
  count: number
}

export interface SourceUsage {
  name: string
  files: number
  bytes: number
}

/** bucket is "yyyy-MM" (UTC); cumulative is the library size at the end of that month. */
export interface LibraryGrowth {
  bucket: string
  seriesAdded: number
  cumulative: number
}

export interface SeriesSize {
  seriesId: number
  title: string
  coverUrl: string | null
  files: number
  bytes: number
}

export interface LibraryComposition {
  totals: LibraryCompositionTotals
  byType: NamedCount[]
  byStatus: NamedCount[]
  bySource: SourceUsage[]
  topGenres: NamedCount[]
  growth: LibraryGrowth[]
  largest: SeriesSize[]
}

/** No userId: the library is shared, and root-folder visibility is applied server-side. */
export function useLibraryComposition(enabled = true) {
  return useQuery({
    queryKey: ['stats', 'library'],
    queryFn: () => api<LibraryComposition>('/stats/library'),
    enabled,
    staleTime: 60_000,
  })
}

// ---- Progress --------------------------------------------------------------

export interface Achievement {
  key: string
  name: string
  description: string
  track: 'Reader' | 'Library'
  icon: string
  graded: boolean
  hidden: boolean
  /** Highest tier earned, 0 for none. */
  tier: number
  tierName: string | null
  value: number
  /** What the next tier needs, or null at the top. */
  nextThreshold: number | null
  tiers: number[]
  unlockedAt: string | null
  /** Set only on stored rows; posted back to stamp the unlock as seen. */
  unlockId: number | null
}

export interface LevelInfo {
  level: number
  xp: number
  intoLevel: number
  levelSpan: number
  nextLevelXp: number
  /** 0..1 through the current level. */
  progress: number
}

export interface ReadingGoal {
  id: number
  period: 'Day' | 'Week' | 'Month' | 'Year'
  metric: 'Chapters' | 'Minutes' | 'SeriesFinished'
  target: number
  progress: number
}

export interface ProgressSummary {
  enabled: boolean
  showStreaks: boolean
  level: LevelInfo
  chaptersRead: number
  readingSeconds: number
  seriesFinished: number
  daysRead: number
  currentStreak: number
  longestStreak: number
  earned: number
  total: number
  recent: Achievement[]
  goals: ReadingGoal[]
  /** Unlocks the user has not been shown yet. */
  unseen: Achievement[]
}

export interface HeatmapDay {
  date: string
  chapters: number
  seconds: number
}

export interface LeaderboardRow {
  userId: number
  name: string
  level: number
  chaptersRead: number
  currentStreak: number
}

export interface ProgressSettings {
  enabled: boolean
  showStreaks: boolean
  showOnLeaderboard: boolean
  /** IANA id, or "" for UTC. */
  timeZone: string
}

export function useProgressSummary(userId?: number, enabled = true) {
  return useQuery({
    queryKey: ['progress', 'summary', userId ?? 'me'],
    queryFn: () => api<ProgressSummary>(`/progress/summary${forUser(userId)}`),
    enabled,
    staleTime: 30_000,
  })
}

export function useAchievements(userId?: number, enabled = true) {
  return useQuery({
    queryKey: ['progress', 'achievements', userId ?? 'me'],
    queryFn: () => api<Achievement[]>(`/progress/achievements${forUser(userId)}`),
    enabled,
    placeholderData: keepPreviousData,
  })
}

export function useReadingHeatmap(userId?: number, enabled = true) {
  return useQuery({
    queryKey: ['progress', 'heatmap', userId ?? 'me'],
    queryFn: () => api<HeatmapDay[]>(`/progress/heatmap${forUser(userId)}`),
    enabled,
    placeholderData: keepPreviousData,
  })
}

export function useLeaderboard(enabled = true) {
  return useQuery({
    queryKey: ['progress', 'leaderboard'],
    queryFn: () => api<LeaderboardRow[]>('/progress/leaderboard'),
    enabled,
    staleTime: 60_000,
  })
}

export function useProgressSettings() {
  return useQuery({
    queryKey: ['progress', 'settings'],
    queryFn: () => api<ProgressSettings>('/progress/settings'),
    staleTime: 5 * 60_000,
  })
}

export function useSaveProgressSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (settings: ProgressSettings) =>
      api<ProgressSettings>('/progress/settings', {
        method: 'PUT',
        body: JSON.stringify(settings),
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData(['progress', 'settings'], saved)
      // Every surface depends on the switches and on which calendar days are bucketed into.
      queryClient.invalidateQueries({ queryKey: ['progress'] })
    },
  })
}

export function useSaveReadingGoal() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (goal: { period: string; metric: string; target: number }) =>
      api<ReadingGoal[]>('/progress/goals', { method: 'PUT', body: JSON.stringify(goal) }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['progress'] }),
  })
}

export function useDeleteReadingGoal() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api(`/progress/goals/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['progress'] }),
  })
}

export function useMarkAchievementsSeen() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (ids: number[]) =>
      api('/progress/achievements/seen', { method: 'POST', body: JSON.stringify({ ids }) }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['progress', 'summary'] }),
  })
}

export function useNotifications() {
  return useQuery({
    queryKey: ['notifications'],
    queryFn: () => api<NotificationDto[]>('/notifications'),
  })
}

export function useCreateNotification() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (value: NotificationRequest) =>
      api<NotificationDto>('/notifications', { method: 'POST', body: JSON.stringify(value) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['notifications'] })
    },
  })
}

export function useUpdateNotification() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, value }: { id: number; value: NotificationRequest }) =>
      api<NotificationDto>(`/notifications/${id}`, { method: 'PUT', body: JSON.stringify(value) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['notifications'] })
    },
  })
}

export function useDeleteNotification() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/notifications/${id}`, { method: 'DELETE' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['notifications'] })
    },
  })
}

export function useTestNotification() {
  return useMutation({
    mutationFn: (value: NotificationRequest) =>
      api<{ success: boolean }>('/notifications/test', {
        method: 'POST',
        body: JSON.stringify(value),
      }),
  })
}

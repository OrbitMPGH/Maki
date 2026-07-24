import {
  keepPreviousData,
  useInfiniteQuery,
  useMutation,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query'
import { api, getInitialize } from './client'
import type {
  AddSeriesRequest,
  ChapterDto,
  MetadataLink,
  MetadataSearchResult,
  NotificationDto,
  NotificationRequest,
  QueueHistoryDto,
  RootFolder,
  SeriesDto,
  SeriesFileDto,
  SeriesScrobbleDto,
  SourceMappingDto,
  UpdateSettingsDto,
  UpdateStatusDto,
} from './types'

export function useSeries() {
  return useQuery({
    queryKey: ['series'],
    queryFn: () => api<SeriesDto[]>('/series'),
  })
}

export function useSeriesDetail(id: number) {
  return useQuery({
    queryKey: ['series', id],
    queryFn: () => api<SeriesDto>(`/series/${id}`),
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
  coverUrl: string | null
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
}

export interface RecommendationRequest {
  /** MangaBaka ids to base picks on. Omit/empty = the whole library. */
  seedIds?: number[]
  filters?: RecommendationFilters
  /** -1 (mainstream) … 0 (neutral) … +1 (hidden gems). */
  obscurity?: number
  refresh?: boolean
}

/** Pages through the server's cached recommendation pool ("Show more" = fetchNextPage). */
export function useRecommendations(request: RecommendationRequest) {
  return useInfiniteQuery({
    queryKey: ['recommendations', request],
    queryFn: ({ pageParam }) =>
      api<RecommendationsResult>('/recommendations', {
        method: 'POST',
        // A refresh recomputes the pool — only bust the cache on the first page, so
        // deeper pages read from the pool that page 0 just rebuilt.
        body: JSON.stringify({
          ...request,
          page: pageParam,
          refresh: pageParam === 0 ? request.refresh : false,
        }),
      }),
    initialPageParam: 0,
    getNextPageParam: (last) => (last.hasMore ? last.page + 1 : undefined),
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
}

/** Expanded ("Show more") request for a single rail: same feed, user filters, higher limit. */
export interface DiscoverFeedRequest {
  feed: string
  genre?: string | null
  filters?: RecommendationFilters
  limit?: number
}

/**
 * Catalogue-browse rails for the Discover tab (independent of the library). Bump `refreshNonce`
 * (e.g. from a Refresh button) to recompute the server-side cache; nonce 0 reads the cache.
 */
export function useDiscover(refreshNonce = 0) {
  return useQuery({
    queryKey: ['discover-rails', refreshNonce],
    queryFn: () =>
      api<DiscoverRail[]>(`/recommendations/discover${refreshNonce > 0 ? '?refresh=true' : ''}`),
    staleTime: 60 * 60 * 1000,
    retry: false,
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

/** Free-text Discover search: a plot description, a mood, or just a title. */
export interface DiscoverSearchRequest {
  query: string
  filters?: RecommendationFilters
  limit?: number
}

export interface DiscoverSearchResponse {
  /** `semantic` = matched on meaning; `title` = fell back to the title index. */
  mode: 'semantic' | 'title'
  items: RecommendationItem[]
}

/**
 * Searches the catalogue by meaning. Disabled until the query has some substance — a one- or
 * two-character query is noise to the embedding model and would just scan for nothing.
 */
export function useDiscoverSearch(request: DiscoverSearchRequest | null) {
  const enabled = (request?.query.trim().length ?? 0) >= 3
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

/** Tag names for the Discover tag filter (empty until the embedding index is built). */
export function useRecommendationTags() {
  return useQuery({
    queryKey: ['recommendation-tags'],
    queryFn: () => api<string[]>('/recommendations/tags'),
    staleTime: 12 * 60 * 60 * 1000,
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
 *  upstream fetch failed (Jikan/MAL outage) — distinct from an empty array (fetched fine,
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
  /** Active embedding model: "base" (default, ~240 MB RAM) or "large" (higher quality, ~500 MB RAM). */
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

/** Switches the embedding model ("base"/"large") live — downloads the model + index, no restart. */
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
 * local dump isn't available — a supplementary "Related" rail, not a core feature.
 */
export function useSeriesRelated(seriesId: number) {
  return useQuery({
    queryKey: ['series-related', seriesId],
    queryFn: () => api<RecommendationItem[]>(`/series/${seriesId}/related`),
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

/** Cached, instant — reflects the last CheckForUpdatesJob run (or a manual check-now). */
export function useUpdateStatus() {
  return useQuery({
    queryKey: ['system', 'update'],
    queryFn: () => api<UpdateStatusDto>('/system/update'),
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
}

export function useSourcePriority() {
  return useQuery({
    queryKey: ['settings', 'sources', 'priority'],
    queryFn: () => api<SourcePrioritySettings>('/settings/sources/priority'),
  })
}

export function useSaveSourcePriority() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (order: string[]) =>
      api<SourcePrioritySettings>('/settings/sources/priority', {
        method: 'PUT',
        body: JSON.stringify({ order }),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'sources', 'priority'] })
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
    queryFn: () => api<{ apiKey: string; port: number }>('/settings/general'),
  })
}

export function useRootFolders() {
  return useQuery({
    queryKey: ['rootfolders'],
    queryFn: () => api<RootFolder[]>('/rootfolder'),
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
    // back on this SPA — not the API host, which can differ (dev: SPA :5173 / API :8990).
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
// and JSON-parses the body, both wrong for a zip blob / multipart form.
export async function downloadBackup(name: string): Promise<void> {
  const init = await getInitialize()
  const res = await fetch(`${init.apiRoot}/system/backups/${encodeURIComponent(name)}`, {
    headers: { 'X-Api-Key': init.apiKey },
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
        headers: { 'X-Api-Key': init.apiKey },
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

// ---- Rewind ----------------------------------------------------------------

export interface RewindTotals {
  chaptersRead: number
  volumesRead: number
  chaptersDownloaded: number
  seriesAdded: number
  seriesRemoved: number
  seriesFinished: number
  seriesDropped: number
}

/** bucket is "yyyy-MM" (month granularity) or "yyyy-MM-dd" (ranges ≤ 62 days). */
export interface RewindTimelinePoint {
  bucket: string
  chaptersRead: number
  chaptersDownloaded: number
  seriesAdded: number
}

export interface RewindSeriesStat {
  seriesId: number | null
  title: string
  count: number
}

export interface RewindWeightedName {
  name: string
  weight: number
}

export interface RewindSeriesEvent {
  seriesId: number | null
  title: string
  at: string
}

export interface RewindDroppedSeries {
  seriesId: number | null
  title: string
  lastProgressAt: string
  maxChapter: number
}

export interface RewindStats {
  from: string
  to: string
  readTrackingAvailable: boolean
  totals: RewindTotals
  timeline: RewindTimelinePoint[]
  topRead: RewindSeriesStat[]
  leastRead: RewindSeriesStat[]
  topGenres: RewindWeightedName[]
  topTags: RewindWeightedName[]
  finished: RewindSeriesEvent[]
  added: RewindSeriesEvent[]
  removed: RewindSeriesEvent[]
  dropped: RewindDroppedSeries[]
}

export function useRewindYears() {
  return useQuery({
    queryKey: ['rewind', 'years'],
    queryFn: () => api<number[]>('/rewind/years'),
  })
}

/** from/to are inclusive local dates (yyyy-MM-dd); the browser's UTC offset is sent along so day/month buckets match the user's calendar. */
export function useRewindStats(from: string, to: string) {
  return useQuery({
    queryKey: ['rewind', from, to],
    queryFn: () =>
      api<RewindStats>(
        `/rewind/stats?from=${from}&to=${to}&utcOffsetMinutes=${new Date().getTimezoneOffset()}`,
      ),
    placeholderData: keepPreviousData,
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

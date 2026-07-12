import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'
import type {
  AddSeriesRequest,
  ChapterDto,
  MetadataSearchResult,
  QueueItemDto,
  RootFolder,
  SeriesDto,
  SourceMappingDto,
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
}

export interface RecommendationsResult {
  related: RecommendationItem[]
  similar: RecommendationItem[]
  generatedAt: string
}

export interface RecommendationFilters {
  yearMin?: number | null
  yearMax?: number | null
  types?: string[]
  statuses?: string[]
  minRating?: number | null
}

export interface RecommendationRequest {
  /** MangaBaka ids to base picks on. Omit/empty = the whole library. */
  seedIds?: number[]
  filters?: RecommendationFilters
  /** -1 (mainstream) … 0 (neutral) … +1 (hidden gems). */
  obscurity?: number
  refresh?: boolean
}

export function useRecommendations(request: RecommendationRequest) {
  return useQuery({
    queryKey: ['recommendations', request],
    queryFn: () =>
      api<RecommendationsResult>('/recommendations', {
        method: 'POST',
        body: JSON.stringify(request),
      }),
    staleTime: 60 * 60 * 1000,
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
}

export function useRecommendationIndex() {
  return useQuery({
    queryKey: ['recommendation-index'],
    queryFn: () => api<RecommendationIndexStatus>('/settings/recommendations'),
    // Poll quickly while an index pass is running; back off when idle.
    refetchInterval: (query) => (query.state.data?.running ? 2000 : false),
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

export function useQueue(includeCompleted = false) {
  return useQuery({
    queryKey: ['queue', includeCompleted],
    queryFn: () => api<QueueItemDto[]>(`/queue?includeCompleted=${includeCompleted}`),
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
    },
  })
}

export function useScrobbleAuthStart() {
  return useMutation({
    mutationFn: (service: string) => api<{ url: string }>(`/scrobble/auth/${service}/start`),
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

export interface ScrobbleSettings {
  aniListClientId: string | null
  aniListClientSecret: string | null
  malClientId: string | null
  malClientSecret: string | null
  mangaBakaToken: string | null
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

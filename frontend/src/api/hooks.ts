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

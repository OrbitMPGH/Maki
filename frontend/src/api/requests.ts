import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export type SeriesRequestKind = 'NewSeries' | 'Chapters'
export type SeriesRequestStatus = 'Pending' | 'Approved' | 'Rejected'

export interface SeriesRequest {
  id: number
  kind: SeriesRequestKind
  status: SeriesRequestStatus
  userId: number
  requestedBy: string
  metadataProviderId: string | null
  seriesId: number | null
  title: string
  coverUrl: string | null
  year: number | null
  /** Both null means "everything": the common case, and all a new-series request can mean. */
  chapterStart: number | null
  chapterEnd: number | null
  note: string | null
  created: string
  resolvedAt: string | null
  resolvedBy: string | null
  resolutionNote: string | null
  queuedCount: number | null
  /** Set when an admin adjusted the range. The flag for the two `original*` fields below. */
  editedAt: string | null
  editedBy: string | null
  /** What the requester originally asked for; null unless `editedAt` is set. */
  originalChapterStart: number | null
  originalChapterEnd: number | null
}

export interface CreateSeriesRequestBody {
  kind: SeriesRequestKind
  metadataProviderId?: string | null
  seriesId?: number | null
  chapterStart?: number | null
  chapterEnd?: number | null
  note?: string | null
}

export type RequestFilter = 'pending' | 'resolved' | 'all'

/** Admins get every request; everyone else gets their own. The server decides which, not this. */
export function useSeriesRequests(status: RequestFilter = 'all') {
  return useQuery({
    queryKey: ['requests', status],
    queryFn: () => api<SeriesRequest[]>(`/requests?status=${status}`),
  })
}

/** Admin-only; used for the nav badge. Returns 0 rather than erroring for everyone else. */
export function usePendingRequestCount(enabled: boolean) {
  return useQuery({
    queryKey: ['requests', 'pending-count'],
    queryFn: () => api<{ count: number }>('/requests/pendingcount'),
    enabled,
    refetchInterval: 120_000,
  })
}

export function useCreateSeriesRequest() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: CreateSeriesRequestBody) =>
      api<SeriesRequest>('/requests', { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['requests'] }),
  })
}

/**
 * Admin-only: narrow (or widen) what a pending request asks for. Both bounds go every time, since a
 * partial update could not express "drop the upper bound" without a tri-state.
 */
export function useEditSeriesRequest() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({
      id,
      ...body
    }: {
      id: number
      chapterStart: number | null
      chapterEnd: number | null
    }) => api<SeriesRequest>(`/requests/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['requests'] }),
  })
}

export function useApproveSeriesRequest() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({
      id,
      ...body
    }: {
      id: number
      rootFolderId?: number | null
      monitorNewItems?: string
      note?: string | null
    }) => api<SeriesRequest>(`/requests/${id}/approve`, { method: 'POST', body: JSON.stringify(body) }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['requests'] })
      // Approving adds a series and queues chapters, both of which other pages are showing.
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['queue'] })
    },
  })
}

export function useRejectSeriesRequest() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, note }: { id: number; note?: string | null }) =>
      api<SeriesRequest>(`/requests/${id}/reject`, { method: 'POST', body: JSON.stringify({ note }) }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['requests'] }),
  })
}

export function useDeleteSeriesRequest() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/requests/${id}`, { method: 'DELETE' }),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['requests'] }),
  })
}

/** "Ch. 12–40", "Ch. 12 onwards", "All chapters": one label for every combination of bounds. */
export function chapterRangeLabel(start: number | null, end: number | null): string {
  if (start == null && end == null) return 'All chapters'
  if (start != null && end != null) return start === end ? `Ch. ${start}` : `Ch. ${start}–${end}`
  if (start != null) return `Ch. ${start} onwards`
  return `Up to ch. ${end}`
}

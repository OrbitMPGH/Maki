import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, getInitialize } from './client'

/** The first chapter of a series that is not in the library, fetched from whichever source has it. */
export interface SeriesPreview {
  providerId: number
  status: 'searching' | 'fetching' | 'ready' | 'failed'
  error: string | null
  sourceName: string | null
  sourceDisplayName: string | null
  chapterLabel: string | null
  pageCount: number | null
  /** Changes when the server switches source, so page URLs built on it never serve stale art. */
  version: string | null
  /** One entry per page, true once that page can be requested. */
  ready: boolean[]
}

const key = (providerId: string) => ['series-preview', providerId]

export function useStartSeriesPreview() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (providerId: string) =>
      api<SeriesPreview>(`/preview/${encodeURIComponent(providerId)}`, { method: 'POST' }),
    onSuccess: (snapshot, providerId) => {
      queryClient.setQueryData(key(providerId), snapshot)
    },
  })
}

/** Polls while the server is still finding a source or pulling pages down. */
export function useSeriesPreview(providerId: string, enabled: boolean) {
  return useQuery({
    queryKey: key(providerId),
    queryFn: () => api<SeriesPreview>(`/preview/${encodeURIComponent(providerId)}`),
    enabled,
    retry: false,
    refetchInterval: (query) => {
      const status = query.state.data?.status
      return status === 'searching' || status === 'fetching' ? 1000 : false
    },
  })
}

/** Tells the server nobody is looking any more, so a fetch still running can stop. */
export function releaseSeriesPreview(providerId: string) {
  void api(`/preview/${encodeURIComponent(providerId)}`, { method: 'DELETE' }).catch(() => {})
}

export async function previewPageUrl(providerId: string, page: number, version: string): Promise<string> {
  const init = await getInitialize()
  return `${init.apiRoot}/preview/${encodeURIComponent(providerId)}/page/${page}?v=${encodeURIComponent(version)}`
}

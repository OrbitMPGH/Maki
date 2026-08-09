import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { ReaderPrefs } from '../pages/reader/prefs'
import { api } from './client'

/** The MangaBaka type vocabulary a profile can claim. Mirrors `SeriesTypes.All` on the server. */
export const SERIES_TYPES = ['manga', 'manhwa', 'manhua', 'oel', 'other'] as const

export const SERIES_TYPE_LABELS: Record<string, string> = {
  manga: 'Manga',
  manhwa: 'Manhwa',
  manhua: 'Manhua',
  oel: 'OEL / western',
  other: 'Other',
}

export interface ReadingProfile {
  id: number
  name: string
  prefs: ReaderPrefs
  /** Series types this profile is picked for automatically. Empty means "only when I pin it". */
  seriesTypes: string[]
}

export type ReadingProfileInput = Pick<ReadingProfile, 'name' | 'prefs' | 'seriesTypes'>

export function useReadingProfiles() {
  return useQuery({
    queryKey: ['reading-profiles'],
    queryFn: () => api<ReadingProfile[]>('/readingprofiles'),
    staleTime: 60_000,
  })
}

/**
 * Every write invalidates the reader manifest as well as the list: a profile is what the reader is
 * currently rendering with, so retuning one from the settings page has to reach an open reader.
 */
function useProfileMutation<TArgs>(fn: (args: TArgs) => Promise<unknown>) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: fn,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['reading-profiles'] })
      void queryClient.invalidateQueries({ queryKey: ['reader-manifest'] })
    },
  })
}

export function useCreateReadingProfile() {
  return useProfileMutation((input: ReadingProfileInput) =>
    api<ReadingProfile>('/readingprofiles', { method: 'POST', body: JSON.stringify(input) }),
  )
}

export function useUpdateReadingProfile() {
  return useProfileMutation(({ id, ...input }: ReadingProfileInput & { id: number }) =>
    api<ReadingProfile>(`/readingprofiles/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  )
}

export function useDeleteReadingProfile() {
  return useProfileMutation((id: number) => api(`/readingprofiles/${id}`, { method: 'DELETE' }))
}

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

/**
 * What the anime-resume resolver worked out for one manga: which anime covers it, how far, and
 * where to pick the manga back up. Shared shape between the series page, the Discover modal and
 * the Home rail; the extra library-only fields live on {@link SeriesAnimeResume}.
 *
 * `inLibrarySeriesId` is set only on the catalogue shape `recommendations/detail/{id}` sends
 * (`GET api/v1/recommendations/detail/{id}`); the series endpoint below never sets it, since the
 * caller already knows which series it asked about.
 */
export interface AnimeResume {
  animeTitle: string
  services: string[]
  score: number | null
  basis: 'allSeasons' | 'seasonCount'
  coveredTo: number
  resumeAt: number
  coveredLabel: string | null
  nextLabel: string | null
  nextFrom: number | null
  nextTo: number | null
  inLibrarySeriesId?: number | null
}

/** The series-page shape: {@link AnimeResume} plus what the library already knows about progress. */
export interface SeriesAnimeResume extends AnimeResume {
  readTo: number | null
  resumeChapterId: number | null
  resumeChapterLabel: string | null
  resumeDownloaded: boolean
  unmarkedCount: number
}

/** One poster on Home's "Continue from the anime" rail. */
export interface HomeAnimeResumeItem {
  seriesId: number
  seriesTitle: string
  coverUrl: string | null
  animeTitle: string
  score: number | null
  coveredTo: number
  coveredLabel: string | null
  resumeChapterId: number | null
  resumeChapterLabel: string | null
}

/**
 * The series page's anime-resume callout data, or null when there is nothing to say: no matched
 * anime, opted out of anime signals, already read past the anime, or dismissed at this coverage.
 * A 204 from the server means the same thing and is normalized to null here.
 */
export function useSeriesAnimeResume(seriesId: number, enabled = true) {
  return useQuery({
    queryKey: ['anime-resume', 'series', seriesId],
    queryFn: () =>
      api<SeriesAnimeResume | undefined>(`/series/${seriesId}/anime-resume`).then((r) => r ?? null),
    enabled: enabled && Number.isFinite(seriesId) && seriesId > 0,
  })
}

export interface ApplyAnimeResumeResult {
  updated: number
  resumeChapterId: number | null
  coveredTo: number
}

/**
 * Marks chapters 1..coveredTo watched (or, with `markWatched: false`, only raises the reading
 * baseline so "Read ch. N+1" doesn't inflate stats on the way past). Invalidates the same queries
 * as {@link useSetChaptersState} plus `['anime-resume']`: read state, the series row and Home's
 * reading rails all depend on it, and the callout itself needs to disappear or re-render.
 */
export function useApplyAnimeResume(seriesId: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: { markWatched: boolean; coveredTo?: number }) =>
      api<ApplyAnimeResumeResult>(`/series/${seriesId}/anime-resume/apply`, {
        method: 'POST',
        body: JSON.stringify(body),
      }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['reader-progress', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['reader-continue', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series', seriesId] })
      void queryClient.invalidateQueries({ queryKey: ['series'] })
      void queryClient.invalidateQueries({ queryKey: ['home'] })
      void queryClient.invalidateQueries({ queryKey: ['anime-resume'] })
    },
  })
}

/**
 * Dismisses the callout ("Not this anime?"), or undoes that with the same route's DELETE. The
 * server keeps it dismissed until the covered range changes, same idea as `useHideHomeReading`.
 */
export function useDismissAnimeResume(seriesId: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ undo = false }: { undo?: boolean } = {}) =>
      api<void>(`/series/${seriesId}/anime-resume/dismiss`, { method: undo ? 'DELETE' : 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['anime-resume'] })
      void queryClient.invalidateQueries({ queryKey: ['home', 'from-anime'] })
    },
  })
}

/** Home's "Continue from the anime" rail: series whose anime is done but the manga isn't caught up. */
export function useHomeFromAnime(limit = 12, enabled = true) {
  return useQuery({
    queryKey: ['home', 'from-anime', limit],
    queryFn: () => api<HomeAnimeResumeItem[]>(`/home/from-anime?limit=${limit}`),
    enabled,
    staleTime: 60_000,
  })
}

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client'

export interface FeedbackState {
  mangaBakaId: number
  suppression: 'none' | 'hidden' | 'dismissed'
  exposure: string[]
  dismissedUntilUtc: string | null
  revision: number
  title: string | null
  sentiment: 'none' | 'liked' | 'disliked'
  coverUrl: string | null
  genres: string[] | null
  updatedAtUtc: string | null
}

export interface FeedbackActivity {
  id: number
  mangaBakaId: number
  title: string | null
  action: string
  occurredAtUtc: string
  stateRevision: number
  dismissedUntilUtc: string | null
  queueEffect: string
  tasteEffect: string
  coverUrl: string | null
}

export interface FeedbackPage<T> {
  items: T[]
  nextCursor: number | null
  feedbackRevision: number
  signalRevision: number
}

/** One thing the titles a reader pushed down have in common. Display only; see TasteAvoidanceService. */
export interface AvoidanceLabel {
  label: string
  kind: 'tag' | 'genre'
  support: number
  share: number
  /** The same share of the reader's own shelf. A chip only appears when it is well below `share`. */
  positiveShare: number
  positiveSupport: number
  shelfCount: number
  examples: { mangaBakaId: number; title: string }[]
}

export interface FeedbackLabData {
  capabilities: {
    feedback: boolean; signalOverrides: boolean; labUi: boolean; personalAddWeighting: boolean
    animeSignals: boolean
  }
  versions: { feedbackRevision: number; signalRevision: number }
  rankingMode: 'semantic' | 'fallback'
  summary: {
    visibleShelf: number; personalAdds: number; ratedSources: number; readSources: number
    excluded: number; hidden: number; dismissed: number; exposed: number; liked: number; disliked: number
    pushingDown: number
  }
  avoids: AvoidanceLabel[]
  sources: {
    mangaBakaId: number; title: string; addedAtUtc: string | null; rating: number | null
    coverUrl: string | null; genres: string[]; isRead: boolean; excluded: boolean
  }[]
  activity: FeedbackPage<FeedbackActivity>
}

export interface FeedbackMutation {
  changed: boolean
  eventId: number | null
  state: FeedbackState
  feedbackRevision: number
  signalRevision: number
  queueEffect: string
  tasteEffect: string
  feedbackEffect: string
}

export interface FranchiseFeedbackResult {
  changed: number
  titles: { mangaBakaId: number; title: string | null }[]
  feedbackRevision: number
}

export interface SignalOverrideState {
  mangaBakaId: number
  ignoreAsSeed: boolean
  revision: number
}

export interface SignalOverrideMutation {
  changed: boolean
  state: SignalOverrideState
  signalRevision: number
}

const affectedKeys = [
  'feedback-lab', 'feedback-states', 'feedback-state', 'feedback-activity', 'signal-overrides',
  'recommendations', 'discover-recent-activity', 'discover-side-interests',
  'discover-cohort', 'discover-rails', 'discover-feed', 'discover-genres',
  'taste-insights', 'taste-profile', 'series-related', 'series-similar', 'home',
  'anime-signals', 'custom-rail-items',
]
const outputKeys = [
  'recommendations', 'discover-recent-activity', 'discover-side-interests',
  'discover-cohort', 'discover-rails', 'discover-feed', 'discover-genres',
  'taste-insights', 'series-related', 'series-similar', 'custom-rail-items',
]
const pendingOptimistic = new Map<number, string>()

function withoutTitle(value: unknown, id: number): unknown {
  if (Array.isArray(value)) return value
    .filter((item) => !(item && typeof item === 'object' &&
      'providerId' in item && Number(item.providerId) === id))
    .map((item) => withoutTitle(item, id))
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, withoutTitle(item, id)]))
  }
  return value
}

export function useFeedbackLab() {
  return useQuery({ queryKey: ['feedback-lab'], queryFn: () => api<FeedbackLabData>('/recommendations/feedback-lab') })
}

export function useFeedbackStates(cursor?: number, sort?: 'recent' | 'title') {
  return useQuery({
    queryKey: ['feedback-states', cursor, sort],
    queryFn: () => api<FeedbackPage<FeedbackState>>(
      `/recommendations/feedback?limit=100${cursor ? `&cursor=${cursor}` : ''}${sort ? `&sort=${sort}` : ''}`),
  })
}

export function useFeedbackState(id: number) {
  return useQuery({
    queryKey: ['feedback-state', id], enabled: Number.isSafeInteger(id) && id > 0,
    queryFn: async () => {
      try { return await api<FeedbackState>(`/recommendations/feedback/${id}`) }
      catch (error) {
        if (String(error).includes('404')) return null
        throw error
      }
    },
  })
}

export function useFeedbackActivity(cursor?: number) {
  return useQuery({
    queryKey: ['feedback-activity', cursor],
    queryFn: () => api<FeedbackPage<FeedbackActivity>>(`/recommendations/feedback/activity?limit=40${cursor ? `&cursor=${cursor}` : ''}`),
  })
}

export function useSignalOverrides() {
  return useQuery({ queryKey: ['signal-overrides'], queryFn: () => api<SignalOverrideState[]>('/recommendations/signal-overrides') })
}

function useRefreshRecommendations() {
  const client = useQueryClient()
  return () => { for (const key of affectedKeys) void client.invalidateQueries({ queryKey: [key] }) }
}

export function useMutateFeedback() {
  const refresh = useRefreshRecommendations()
  const client = useQueryClient()
  return useMutation({
    mutationFn: ({ id, action, medium, expectedRevision, clientMutationId }: {
      id: number; action: string; medium?: string; expectedRevision: number; clientMutationId: string
    }) => api<FeedbackMutation>(`/recommendations/feedback/${id}`, {
      method: 'PUT', body: JSON.stringify({ action, medium, expectedRevision, clientMutationId }),
    }),
    onMutate: async (command) => {
      if (!['hide', 'dismiss', 'mark-exposed'].includes(command.action)) return null
      pendingOptimistic.set(command.id, command.clientMutationId)
      const snapshots: [readonly unknown[], unknown, unknown][] = []
      for (const key of outputKeys) {
        await client.cancelQueries({ queryKey: [key] })
        for (const [queryKey, value] of client.getQueriesData({ queryKey: [key] })) {
          const optimistic = withoutTitle(value, command.id)
          client.setQueryData(queryKey, optimistic)
          snapshots.push([queryKey, value, client.getQueryData(queryKey)])
        }
      }
      return snapshots
    },
    onError: (_error, command, snapshots) => {
      if (pendingOptimistic.get(command.id) === command.clientMutationId) {
        pendingOptimistic.delete(command.id)
        for (const [queryKey, value, optimistic] of snapshots ?? []) {
          if (client.getQueryData(queryKey) !== optimistic) continue
          let restored = value
          for (const pendingId of pendingOptimistic.keys()) restored = withoutTitle(restored, pendingId)
          client.setQueryData(queryKey, restored)
        }
      }
      refresh()
    },
    onSuccess: (_result, command) => {
      if (pendingOptimistic.get(command.id) === command.clientMutationId) pendingOptimistic.delete(command.id)
      refresh()
    },
  })
}

/**
 * One suppression applied to every member of a title's franchise. No optimistic removal: the
 * members are not known until the server answers, and the refresh below is what takes them off the
 * rails.
 */
export function useMutateFranchiseFeedback() {
  const refresh = useRefreshRecommendations()
  return useMutation({
    mutationFn: ({ id, action, clientMutationId }: {
      id: number; action: 'hide' | 'dismiss'; clientMutationId: string
    }) => api<FranchiseFeedbackResult>(`/recommendations/feedback/${id}/franchise`, {
      method: 'POST', body: JSON.stringify({ action, clientMutationId }),
    }),
    onSuccess: refresh,
  })
}

export function useUndoFeedback() {
  const refresh = useRefreshRecommendations()
  return useMutation({
    mutationFn: ({ eventId, expectedRevision, clientMutationId }: {
      eventId: number; expectedRevision: number; clientMutationId: string
    }) => api<FeedbackMutation>(`/recommendations/feedback/events/${eventId}/undo`, {
      method: 'POST', body: JSON.stringify({ expectedRevision, clientMutationId }),
    }),
    onSuccess: refresh,
  })
}

export function useMutateSignalOverride() {
  const refresh = useRefreshRecommendations()
  return useMutation({
    mutationFn: ({ id, ignoreAsSeed, expectedRevision, clientMutationId }: {
      id: number; ignoreAsSeed: boolean; expectedRevision: number; clientMutationId: string
    }) => api<SignalOverrideMutation>(`/recommendations/signal-overrides/${id}`, {
      method: 'PUT', body: JSON.stringify({ ignoreAsSeed, expectedRevision, clientMutationId }),
    }),
    onSuccess: refresh,
  })
}

export { affectedKeys }

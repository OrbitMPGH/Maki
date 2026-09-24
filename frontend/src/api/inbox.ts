import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { msg } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { api } from './client'

/**
 * Mirrors `InboxEventType` on the server, in camelCase. Append only — the values are persisted as
 * preference keys, so renaming one drops everybody's stored setting for it.
 */
export type InboxEventType =
  | 'newChapterAvailable'
  | 'smartDownloadQueued'
  | 'chapterDownloaded'
  | 'downloadFailed'
  | 'achievementUnlocked'
  | 'levelUp'
  | 'requestSubmitted'
  | 'requestApproved'
  | 'requestRejected'
  | 'requestEdited'
  | 'healthIssue'
  | 'updateAvailable'
  | 'importFinished'
  | 'backupFinished'
  | 'sourceMatchFinished'
  | 'importListFinished'

export type InboxLevel = 'info' | 'warning' | 'error'

export interface InboxItem {
  id: number
  type: InboxEventType
  level: InboxLevel
  title: string
  body: string
  seriesId: number | null
  chapterId: number | null
  /** A path inside the app, e.g. `/series/42`. Null when there is nowhere to go. */
  url: string | null
  /**
   * The series' poster, when the notification names one that still exists and is visible to you.
   * Resolved server-side per request, so it is null for a deleted series rather than a broken image.
   */
  coverUrl: string | null
  createdAt: string
  read: boolean
}

export interface InboxPage {
  items: InboxItem[]
  unread: number
  /** Pass back as `before` for the next page. Null once the feed is exhausted. */
  nextCursor: number | null
}

export interface InboxPrefs {
  types: Record<string, boolean>
  toasts: boolean
  /**
   * What a series left on "Default" means: "All" (every new chapter) or "Reading" (only series you
   * are partway through). Server-side names, matching `SeriesNotificationMode`.
   */
  seriesDefault: string
}

/**
 * What arrives over SignalR: the row, plus the recipient's new unread count.
 * <p>
 * No `coverUrl` — the push only drives the badge and the toast, neither of which shows one, and the
 * feed is refetched anyway. Resolving a poster on the raise path would mean a query per recipient.
 */
export interface InboxPush extends Omit<InboxItem, 'read' | 'coverUrl'> {
  unread: number
}

/**
 * Grouping for the settings card and the page's filter chips. Purely presentational — the server
 * knows nothing about these buckets, and an event type missing from here simply isn't offered.
 */
/**
 * `id` is the filter value and the React key; `label` is what a reader sees. They were one string
 * until translation made that impossible: a Swedish chip cannot also be the value compared against
 * the stored filter. Descriptors, not strings, for the reason given on the table below.
 */
export const INBOX_CATEGORIES: {
  id: string
  label: MessageDescriptor
  types: InboxEventType[]
  adminOnly?: boolean
}[] = [
  {
    id: 'library',
    label: msg`Library`,
    types: ['newChapterAvailable', 'smartDownloadQueued', 'sourceMatchFinished', 'importListFinished'],
  },
  { id: 'downloads', label: msg`Downloads`, types: ['chapterDownloaded', 'downloadFailed'] },
  { id: 'progress', label: msg`Progress`, types: ['achievementUnlocked', 'levelUp'] },
  {
    id: 'requests',
    label: msg`Requests`,
    types: ['requestSubmitted', 'requestApproved', 'requestRejected', 'requestEdited'],
  },
  {
    id: 'system',
    label: msg`System`,
    types: ['healthIssue', 'updateAvailable', 'importFinished', 'backupFinished'],
    adminOnly: true,
  },
]

/**
 * Descriptors, not strings: this table is built once when the module loads, so a rendered string
 * here would be stuck in whichever language was active at that moment. Render with `useLabel()`.
 */
export const INBOX_TYPE_LABELS: Record<InboxEventType, MessageDescriptor> = {
  newChapterAvailable: msg`New chapters available`,
  smartDownloadQueued: msg`Smart Download queued chapters`,
  chapterDownloaded: msg`Chapters downloaded automatically`,
  downloadFailed: msg`Automatic download failed`,
  achievementUnlocked: msg`Achievement unlocked`,
  levelUp: msg`Level up`,
  requestSubmitted: msg`Somebody filed a request`,
  requestApproved: msg`Your request was approved`,
  requestRejected: msg`Your request was declined`,
  requestEdited: msg`Your request was adjusted`,
  healthIssue: msg`Health issue`,
  updateAvailable: msg`Update available`,
  importFinished: msg`Library import finished`,
  backupFinished: msg`Backup taken`,
  sourceMatchFinished: msg`Source matching finished`,
  importListFinished: msg`Import list finished`,
}

/**
 * Shown under the label on the settings card, same table shape as `INBOX_TYPE_LABELS` and for the
 * same reason: a rendered string here would be frozen in whichever language was active when the
 * module loaded. Only event types that need more than their label carry an entry.
 */
export const INBOX_TYPE_DESCRIPTIONS: Partial<Record<InboxEventType, MessageDescriptor>> = {
  importListFinished: msg`A tracker list sync added or requested series.`,
}

/** Only ever admin-visible, so the settings card hides these for everyone else. */
export const INBOX_ADMIN_ONLY: InboxEventType[] = [
  'requestSubmitted',
  'healthIssue',
  'updateAvailable',
  'importFinished',
  'backupFinished',
]

export function useInbox(filter?: { unreadOnly?: boolean; type?: InboxEventType | null }) {
  const unreadOnly = filter?.unreadOnly ?? false
  const type = filter?.type ?? null

  return useInfiniteQuery({
    queryKey: ['inbox', 'feed', unreadOnly, type],
    initialPageParam: null as number | null,
    queryFn: ({ pageParam }) => {
      const params = new URLSearchParams()
      if (pageParam != null) params.set('before', String(pageParam))
      if (unreadOnly) params.set('unreadOnly', 'true')
      if (type) params.set('type', type)
      const qs = params.toString()
      return api<InboxPage>(`/inbox${qs ? `?${qs}` : ''}`)
    },
    getNextPageParam: (last) => last.nextCursor,
  })
}

/**
 * The header badge. Its own query rather than reading the feed's `unread`, so the bell has a number
 * before anyone opens it. The SignalR push patches this directly; the interval is the safety net for
 * a dropped connection.
 */
export function useInboxUnread() {
  return useQuery({
    queryKey: ['inbox', 'unread'],
    queryFn: () => api<{ count: number }>('/inbox/unread-count'),
    refetchInterval: 120_000,
  })
}

export function useMarkInboxRead() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/inbox/${id}/read`, { method: 'POST' }),
    onSuccess: () => invalidateInbox(queryClient),
  })
}

export function useMarkAllInboxRead() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<{ marked: number }>('/inbox/read-all', { method: 'POST' }),
    onSuccess: () => invalidateInbox(queryClient),
  })
}

export function useDismissInbox() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/inbox/${id}`, { method: 'DELETE' }),
    onSuccess: () => invalidateInbox(queryClient),
  })
}

export function useClearInbox() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<{ deleted: number }>('/inbox', { method: 'DELETE' }),
    onSuccess: () => invalidateInbox(queryClient),
  })
}

/**
 * Always comes back merged, so every event type this build knows has an entry even for a user who
 * has never opened the settings card.
 */
export function useInboxPrefs() {
  return useQuery({
    queryKey: ['inbox', 'prefs'],
    queryFn: () => api<InboxPrefs>('/inbox/prefs'),
    staleTime: 60_000,
  })
}

export function useSaveInboxPrefs() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (prefs: InboxPrefs) =>
      api<InboxPrefs>('/inbox/prefs', { method: 'PUT', body: JSON.stringify(prefs) }),
    onSuccess: (saved) => queryClient.setQueryData(['inbox', 'prefs'], saved),
  })
}

function invalidateInbox(queryClient: ReturnType<typeof useQueryClient>) {
  void queryClient.invalidateQueries({ queryKey: ['inbox'] })
}

import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, apiRootNow } from './client'

/** `confirmed` was set by an admin, `propagated` taken from chapters that carry the group, `suggested` read off the image. */
export type CreditNameSource = 'confirmed' | 'propagated' | 'suggested' | 'none'
export type CreditNoticeKind = 'unknown' | 'group' | 'site'
export type CreditNoticeOverride = 'none' | 'hide' | 'keep'

export interface CreditGroup {
  /** `group:<normalized name>`, `site` or `unnamed`. */
  key: string
  name: string | null
  nameSource: CreditNameSource
  kind: 'group' | 'site' | 'unknown'
  noticeIds: number[]
  notices: number
  series: number
  chapters: number
  chaptersWithoutGroup: number
}

export interface CreditNotice {
  id: number
  /** What readers get now: the override, else the automatic verdict. */
  hidden: boolean
  accepted: boolean
  override: CreditNoticeOverride
  kind: CreditNoticeKind
  groupName: string | null
  groupSource: 'none' | 'propagated' | 'confirmed'
  suggestedName: string | null
  invite: string | null
  excerpt: string | null
  whole: boolean
  /** Share of the page from the top, or from the bottom when `tail`. */
  share: number
  tail: boolean
  chapters: number
  series: number
}

export interface CreditGroupChapter {
  chapterId: number
  label: string
  number: number | null
  volume: number | null
  title: string | null
  isOneShot: boolean
  /** The group the chapter's file carries, null when it has none. */
  group: string | null
  noticeIds: number[]
}

export interface CreditGroupSeries {
  seriesId: number
  title: string
  chapters: CreditGroupChapter[]
}

export interface CreditGroupDetail {
  notices: CreditNotice[]
  series: CreditGroupSeries[]
}

export interface UpdateCreditNotice {
  kind?: 'Unknown' | 'Group' | 'Site'
  /** Empty clears the name; anything else confirms it. */
  groupName?: string
  override?: 'None' | 'Hide' | 'Keep'
}

export interface ApplyCreditGroupsResult {
  chaptersUpdated: number
  filesRenamed: number
  conflicts: number
  warnings: string[]
}

const KEY = ['credit-groups']

export function useCreditGroups(all: boolean, enabled = true) {
  return useQuery({
    queryKey: [...KEY, 'list', all],
    queryFn: () => api<{ groups: CreditGroup[] }>(`/creditgroups?all=${all}`),
    select: (data) => data.groups,
    placeholderData: keepPreviousData,
    enabled,
  })
}

export function useCreditGroupDetail(noticeIds: number[] | null) {
  const ids = noticeIds ?? []
  return useQuery({
    queryKey: [...KEY, 'notices', ids],
    // A POST with the ids in the body: a group can hold hundreds of notices, past what a URL carries.
    queryFn: () =>
      api<CreditGroupDetail>('/creditgroups/notices', { method: 'POST', body: JSON.stringify({ noticeIds: ids }) }),
    // A group whose notices change keeps showing the old ones until the new answer lands.
    placeholderData: keepPreviousData,
    enabled: ids.length > 0,
  })
}

/** Where a notice's image is served from: the region cut from one chapter's page, at most 640 px wide. */
export function creditNoticeImageUrl(id: number): string {
  return `${apiRootNow() ?? '/api/v1'}/creditgroups/notices/${id}/image`
}

export function useUpdateCreditNotice() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, change }: { id: number; change: UpdateCreditNotice }) =>
      api<CreditNotice>(`/creditgroups/notices/${id}`, { method: 'PUT', body: JSON.stringify(change) }),
    onSuccess: (notice) => {
      qc.setQueriesData<CreditGroupDetail>({ queryKey: [...KEY, 'notices'] }, (detail) =>
        detail ? { ...detail, notices: detail.notices.map((n) => (n.id === notice.id ? notice : n)) } : detail,
      )
      void qc.invalidateQueries({ queryKey: KEY })
    },
  })
}

export function useNameCreditNotices() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { noticeIds: number[]; name: string }) =>
      api<{ named: number }>('/creditgroups/name', { method: 'PUT', body: JSON.stringify(body) }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: KEY }),
  })
}

export function useApplyCreditGroups() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (noticeIds: number[]) =>
      api<ApplyCreditGroupsResult>('/creditgroups/apply', { method: 'POST', body: JSON.stringify({ noticeIds }) }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: KEY })
      void qc.invalidateQueries({ queryKey: ['series'] })
    },
  })
}

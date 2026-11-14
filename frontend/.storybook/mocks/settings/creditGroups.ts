import { http, HttpResponse, type HttpHandler } from 'msw'
import type {
  ApplyCreditGroupsResult,
  CreditGroup,
  CreditGroupChapter,
  CreditGroupDetail,
  CreditGroupSeries,
  CreditNotice,
  UpdateCreditNotice,
} from '../../../src/api/creditGroups'
import { api } from '../api'

const notice = (id: number, over: Partial<CreditNotice>): CreditNotice => ({
  id,
  hidden: true,
  accepted: true,
  override: 'none',
  kind: 'group',
  groupName: null,
  groupSource: 'none',
  suggestedName: null,
  invite: null,
  excerpt: null,
  whole: true,
  share: 1,
  tail: false,
  chapters: 10,
  series: 1,
  ...over,
})

export const creditNotices: CreditNotice[] = [
  notice(1, {
    groupName: 'Moonlit Scans',
    groupSource: 'confirmed',
    suggestedName: 'Moonlit Scans',
    invite: 'moonlit',
    excerpt: 'MOONLIT SCANS | Translator: Hana | Cleaner: Ivo | Join us on Discord | Read it first on our site',
    chapters: 26,
    series: 2,
  }),
  notice(2, {
    groupName: 'Moonlit Scans',
    groupSource: 'confirmed',
    suggestedName: 'Moonlit Scan',
    excerpt: 'We are recruiting typesetters | Moonlit Scans',
    whole: false,
    share: 0.28,
    tail: true,
    chapters: 12,
    series: 1,
  }),
  notice(3, {
    groupName: 'Kitsune Translations',
    groupSource: 'propagated',
    suggestedName: 'Kitsune Translation',
    excerpt: 'Kitsune Translations | Support us on Ko-fi',
    chapters: 21,
  }),
  notice(4, {
    kind: 'unknown',
    suggestedName: 'Paper Crane',
    excerpt: 'PAPER CRANE | TL: Mo | PR: Jun | Please do not repost',
    chapters: 9,
  }),
  notice(5, {
    kind: 'unknown',
    suggestedName: 'Paper Crane',
    excerpt: 'Paper Crane Scanlations',
    whole: false,
    share: 0.18,
    chapters: 5,
    hidden: false,
    accepted: false,
  }),
  notice(6, {
    groupName: 'Hollow Lantern',
    groupSource: 'confirmed',
    excerpt: 'Hollow Lantern | Thanks for reading',
    override: 'hide',
    accepted: false,
    chapters: 9,
  }),
  notice(7, {
    kind: 'site',
    excerpt: 'Read the latest chapters first at our site | No ads with premium',
    whole: false,
    share: 0.22,
    tail: true,
    chapters: 40,
    series: 3,
  }),
  notice(8, { kind: 'site', excerpt: 'Just previews, buy the volumes', chapters: 12, series: 2 }),
  notice(9, { kind: 'unknown', excerpt: 'Thank you for reading! | See you next chapter', chapters: 4 }),
  // No chapter that carries it can be read: the image route answers 404.
  notice(99, { kind: 'unknown', excerpt: null, chapters: 2 }),
]

export const creditGroups: CreditGroup[] = [
  {
    key: 'group:moonlit scans',
    name: 'Moonlit Scans',
    nameSource: 'confirmed',
    kind: 'group',
    noticeIds: [1, 2],
    notices: 2,
    series: 2,
    chapters: 38,
    chaptersWithoutGroup: 12,
  },
  {
    key: 'group:kitsune translations',
    name: 'Kitsune Translations',
    nameSource: 'propagated',
    kind: 'group',
    noticeIds: [3],
    notices: 1,
    series: 1,
    chapters: 21,
    chaptersWithoutGroup: 4,
  },
  {
    key: 'group:paper crane',
    name: 'Paper Crane',
    nameSource: 'suggested',
    kind: 'unknown',
    noticeIds: [4, 5],
    notices: 2,
    series: 1,
    chapters: 14,
    // Only a confirmed or carried name gives chapters a group, so a suggested one changes none yet.
    chaptersWithoutGroup: 0,
  },
  {
    key: 'group:hollow lantern',
    name: 'Hollow Lantern',
    nameSource: 'confirmed',
    kind: 'group',
    noticeIds: [6],
    notices: 1,
    series: 1,
    chapters: 9,
    chaptersWithoutGroup: 0,
  },
  {
    key: 'site',
    name: null,
    nameSource: 'none',
    kind: 'site',
    noticeIds: [7, 8],
    notices: 2,
    series: 3,
    chapters: 52,
    chaptersWithoutGroup: 0,
  },
  {
    key: 'unnamed',
    name: null,
    nameSource: 'none',
    kind: 'unknown',
    noticeIds: [9, 99],
    notices: 2,
    series: 1,
    chapters: 6,
    chaptersWithoutGroup: 0,
  },
]

/** `?all=true` adds a notice readers keep, which the default list leaves out. */
export const creditGroupsAll: CreditGroup[] = [
  ...creditGroups.slice(0, 5),
  {
    key: 'group:night owl',
    name: 'Night Owl',
    nameSource: 'suggested',
    kind: 'unknown',
    noticeIds: [10],
    notices: 1,
    series: 1,
    chapters: 3,
    chaptersWithoutGroup: 0,
  },
  creditGroups[5],
]

const SERIES = [
  { seriesId: 1, title: 'Lanterns Over Kasumi' },
  { seriesId: 2, title: 'The Ninth Kitchen' },
  { seriesId: 3, title: 'Saltwind Courier' },
]

function chaptersFor(seriesIndex: number, count: number, ids: number[], group: (n: number) => string | null): CreditGroupChapter[] {
  return Array.from({ length: count }, (_, i) => {
    const n = i + 1
    const volume = Math.floor(i / 8) + 1
    return {
      chapterId: seriesIndex * 1000 + n,
      label: `Vol.${volume} Ch.${n}`,
      number: n,
      volume,
      title: n === 1 ? 'The first lantern' : null,
      isOneShot: false,
      group: group(n),
      noticeIds: ids,
    }
  })
}

/** The detail answer for any set of notice ids: those notices, and every fixture series carrying one. */
export function creditDetailFor(ids: number[], notices: CreditNotice[] = creditNotices): CreditGroupDetail {
  const picked = notices.filter((n) => ids.includes(n.id))
  const seriesCount = Math.max(1, Math.min(3, ...picked.map((n) => n.series)))
  const site = picked.every((n) => n.kind === 'site')
  const series: CreditGroupSeries[] = SERIES.slice(0, seriesCount).map((s, i) => ({
    ...s,
    chapters: chaptersFor(s.seriesId, i === 0 ? 14 : 5, ids, (n) =>
      site ? null : n % 3 === 0 ? null : (picked[0]?.groupName ?? null),
    ),
  }))
  return { notices: picked, series: picked.length ? series : [] }
}

export const creditApplyResult = {
  chaptersUpdated: 11,
  filesRenamed: 9,
  conflicts: 1,
  warnings: ['Vol.2 Ch.12 is open in the reader, so its file keeps its old name until the next rename.'],
} satisfies ApplyCreditGroupsResult

const WIRE = { Unknown: 'unknown', Group: 'group', Site: 'site', None: 'none', Hide: 'hide', Keep: 'keep' } as const

export const creditGroupsHandlers = (groups = creditGroups, all = creditGroupsAll): HttpHandler[] => [
  http.get(api('/creditgroups'), ({ request }) =>
    HttpResponse.json({ groups: new URL(request.url).searchParams.get('all') === 'true' ? all : groups }),
  ),
  http.post(api('/creditgroups/notices'), async ({ request }) => {
    const { noticeIds } = (await request.json()) as { noticeIds: number[] }
    return HttpResponse.json(creditDetailFor(noticeIds))
  }),
  http.put(api('/creditgroups/notices/:id'), async ({ params, request }) => {
    const body = (await request.json()) as UpdateCreditNotice
    const current = creditNotices.find((n) => n.id === Number(params.id)) ?? notice(Number(params.id), {})
    const override = body.override ? WIRE[body.override] : current.override
    const kind = body.kind ? WIRE[body.kind] : body.groupName ? 'group' : current.kind
    const next: CreditNotice = {
      ...current,
      override,
      kind,
      hidden: override === 'none' ? current.accepted : override === 'hide',
      groupName: kind === 'site' ? null : body.groupName !== undefined ? body.groupName || null : current.groupName,
      groupSource: body.groupName ? 'confirmed' : current.groupSource,
    }
    return HttpResponse.json(next)
  }),
  http.put(api('/creditgroups/name'), async ({ request }) => {
    const body = (await request.json()) as { noticeIds: number[] }
    return HttpResponse.json({ named: body.noticeIds.length })
  }),
  http.post(api('/creditgroups/apply'), () => HttpResponse.json(creditApplyResult)),
]

import { http, HttpResponse, type HttpHandler } from 'msw'
import type { RecommendationItem } from '../../src/api/hooks'
import type { RemovedShortlistEntry, ShortlistEntry } from '../../src/api/shortlist'
import { api, daysAgo } from './api'
import { catalogueItem } from './discover/fixtures'

const BASE_ID = 30000
const noContent = () => new HttpResponse(null, { status: 204 })

/** A catalogue card for the id; an id outside {@link catalogueItem}'s range still gets one, so a write never fails. */
const itemFor = (id: number) => catalogueItem(((id - BASE_ID) % 1000 + 1000) % 1000)

function entry(n: number, overrides: Partial<Omit<ShortlistEntry, 'item'>> & { item?: Partial<RecommendationItem> } = {}): ShortlistEntry {
  const { item: itemOverrides, ...rest } = overrides
  const item = { ...catalogueItem(n), ...itemOverrides }
  return {
    mangaBakaId: Number(item.providerId),
    item,
    note: null,
    savedFrom: 'other',
    savedFromDetail: null,
    createdAtUtc: daysAgo(10),
    statusChange: null,
    newChapters: null,
    library: null,
    ...rest,
  }
}

/** A title that finished since it was saved, with chapters added on top. */
export const changedEntry = entry(6, {
  note: 'Wait for the ending before starting.',
  savedFrom: 'rail:trending',
  createdAtUtc: daysAgo(70),
  statusChange: { from: 'Ongoing', to: 'Completed' },
  newChapters: 12,
  item: { type: 'Manga', hasAnime: true, status: 'Completed' },
})

export const hiatusEntry = entry(7, {
  savedFrom: 'importlist',
  savedFromDetail: 'AniList',
  createdAtUtc: daysAgo(400),
  statusChange: { from: 'Ongoing', to: 'Hiatus' },
  item: { type: 'Manga', hasAnime: false },
})

export const ongoingEntry = entry(3, {
  savedFrom: 'search',
  createdAtUtc: daysAgo(20),
  newChapters: 14,
  item: { type: 'Manga', hasAnime: false },
})

export const oldCompletedEntry = entry(10, {
  note: 'Recommended by a friend.',
  savedFrom: 'creator',
  savedFromDetail: 'Haruka Ono',
  createdAtUtc: daysAgo(200),
  item: { type: 'Manga', hasAnime: true },
})

export const inLibraryEntry = entry(0, {
  savedFrom: 'rail:recent',
  createdAtUtc: daysAgo(5),
  library: { seriesId: 3, archived: false },
  item: { type: 'Manga', hasAnime: false },
})

export const hiddenLibraryEntry = entry(12, {
  savedFrom: 'catalogue',
  createdAtUtc: daysAgo(3),
  library: { seriesId: null, archived: false },
  item: { type: 'Manga', hasAnime: false },
})

export const removedEntry: ShortlistEntry = {
  mangaBakaId: 99999,
  item: null,
  title: 'Vanished Title',
  note: null,
  savedFrom: 'other',
  savedFromDetail: null,
  createdAtUtc: daysAgo(30),
  statusChange: null,
  newChapters: null,
  library: null,
}

export const recentEntry = entry(14, {
  savedFrom: 'rail:cohort',
  createdAtUtc: daysAgo(2),
  item: { type: 'Manga', hasAnime: true },
})

export const shortlistEntries: ShortlistEntry[] = [
  changedEntry,
  hiatusEntry,
  ongoingEntry,
  oldCompletedEntry,
  inLibraryEntry,
  hiddenLibraryEntry,
  removedEntry,
  recentEntry,
]

/** The same entries with the catalogue gone, as the server answers before a dump is installed. */
export const shortlistNoDump: ShortlistEntry[] = shortlistEntries.map((e) => ({
  ...e,
  item: null,
  title: e.item?.title ?? e.title ?? null,
  statusChange: null,
  newChapters: null,
}))

const idsOf = (list: ShortlistEntry[]) => [
  ...new Set(list.flatMap((e) => [e.mangaBakaId, ...(e.item ? [Number(e.item.providerId)] : [])])),
]

/**
 * The handlers every story gets: an empty shortlist, so cards render unsaved and any write succeeds.
 * Registered globally because every card on every page reads `shortlist/ids`.
 */
export const shortlistHandlers: HttpHandler[] = [
  http.get(api('/shortlist/ids'), () => HttpResponse.json([])),
  http.get(api('/shortlist'), () => HttpResponse.json([])),
  http.put(api('/shortlist/:id'), noContent),
  http.patch(api('/shortlist/:id'), noContent),
  http.post(api('/shortlist/:id/acknowledge'), noContent),
  http.post(api('/shortlist/:id/restore'), noContent),
  http.delete(api('/shortlist/:id'), noContent),
]

/**
 * A shortlist that remembers writes, for a story that presses the buttons. Give it to
 * `parameters.msw` and call `reset` from the story's `beforeEach`: with `isolate: false` module
 * state outlives a story.
 */
export function shortlistMock(initial: ShortlistEntry[]) {
  let list = initial.map((e) => ({ ...e }))
  const find = (id: number) => list.find((e) => e.mangaBakaId === id || e.item?.providerId === String(id))
  const handlers: HttpHandler[] = [
    http.get(api('/shortlist/ids'), () => HttpResponse.json(idsOf(list))),
    http.get(api('/shortlist'), () => HttpResponse.json(list)),
    http.put(api('/shortlist/:id'), async ({ params, request }) => {
      const id = Number(params.id)
      const body = (await request.json()) as { savedFrom: string; savedFromDetail?: string; note?: string }
      if (!find(id)) {
        list = [
          ...list,
          {
            mangaBakaId: id,
            item: itemFor(id),
            note: body.note ?? null,
            savedFrom: body.savedFrom,
            savedFromDetail: body.savedFromDetail ?? null,
            createdAtUtc: new Date().toISOString(),
            statusChange: null,
            newChapters: null,
            library: null,
          },
        ]
      }
      return noContent()
    }),
    http.patch(api('/shortlist/:id'), async ({ params, request }) => {
      const body = (await request.json()) as { note: string }
      const target = find(Number(params.id))
      if (target) target.note = body.note.trim() || null
      return noContent()
    }),
    http.post(api('/shortlist/:id/acknowledge'), ({ params }) => {
      const target = find(Number(params.id))
      if (target) {
        target.statusChange = null
        target.newChapters = null
      }
      return noContent()
    }),
    http.delete(api('/shortlist/:id'), ({ params }) => {
      const target = find(Number(params.id))
      if (!target) return noContent()
      list = list.filter((e) => e !== target)
      return HttpResponse.json({
        mangaBakaId: target.mangaBakaId,
        title: target.item?.title ?? target.title ?? '',
        note: target.note,
        savedFrom: target.savedFrom,
        savedFromDetail: target.savedFromDetail,
        savedStatus: target.item?.status ?? null,
        savedTotalChapters: target.item?.totalChapters ?? null,
        createdAtUtc: target.createdAtUtc,
      } satisfies RemovedShortlistEntry)
    }),
    http.post(api('/shortlist/:id/restore'), async ({ params, request }) => {
      const body = (await request.json()) as RemovedShortlistEntry
      const id = Number(params.id)
      if (!find(id)) {
        list = [
          ...list,
          {
            mangaBakaId: id,
            item: itemFor(id),
            note: body.note,
            savedFrom: body.savedFrom,
            savedFromDetail: body.savedFromDetail,
            createdAtUtc: body.createdAtUtc,
            statusChange: null,
            newChapters: null,
            library: null,
          },
        ]
      }
      return noContent()
    }),
  ]
  return {
    handlers,
    reset: () => {
      list = initial.map((e) => ({ ...e }))
    },
    /** The writes the story made, for assertions. */
    snapshot: () => list,
  }
}

import { http, HttpResponse } from 'msw'
import type { SeriesRequest } from '../../../src/api/requests'
import { api, cover, daysAgo, hoursAgo } from '../api'

const request = (id: number, over: Partial<SeriesRequest>): SeriesRequest => ({
  id,
  kind: 'NewSeries',
  status: 'Pending',
  userId: 2,
  requestedBy: 'Rin',
  metadataProviderId: String(20000 + id),
  seriesId: null,
  title: 'Starlit Ferry',
  coverUrl: null,
  year: 2023,
  chapterStart: null,
  chapterEnd: null,
  note: null,
  created: hoursAgo(id * 5),
  resolvedAt: null,
  resolvedBy: null,
  resolutionNote: null,
  queuedCount: null,
  editedAt: null,
  editedBy: null,
  originalChapterStart: null,
  originalChapterEnd: null,
  ...over,
})

export const pendingRequests: SeriesRequest[] = [
  request(1, {
    title: 'Starlit Ferry',
    coverUrl: cover('Starlit Ferry'),
    note: 'A friend recommended this one.\nIt has a short run, so it should be quick to fetch.',
  }),
  request(2, {
    kind: 'Chapters',
    seriesId: 6,
    title: 'Ashen Crown Regression',
    coverUrl: cover(6),
    year: 2020,
    requestedBy: 'Theo',
    userId: 3,
    chapterStart: 151,
    chapterEnd: 203,
    originalChapterStart: 151,
    originalChapterEnd: 203,
  }),
  request(3, {
    title: 'Moth Orchard: The Collected Tales of the Lantern Wardens and Their Long Winter',
    coverUrl: cover('Moth Orchard'),
    year: null,
    requestedBy: 'Mina',
    userId: 4,
    chapterStart: 12,
    chapterEnd: null,
    editedAt: hoursAgo(2),
    editedBy: 'Maki',
    originalChapterStart: null,
    originalChapterEnd: null,
  }),
  request(4, {
    kind: 'Chapters',
    seriesId: 9,
    title: 'Grave Robber of the Seventh Hall',
    coverUrl: cover(9),
    year: 2024,
    chapterStart: 5,
    chapterEnd: 5,
    originalChapterStart: 5,
    originalChapterEnd: 5,
  }),
]

export const processingRequest = request(5, {
  status: 'Processing',
  title: 'Tidewater Almanac',
  coverUrl: cover('Tidewater Almanac'),
  year: 2021,
})

export const resolvedRequests: SeriesRequest[] = [
  request(6, {
    status: 'Approved',
    kind: 'Chapters',
    seriesId: 1,
    title: 'Lanterns Over Kasumi',
    coverUrl: cover(1),
    year: 2021,
    chapterStart: 132,
    chapterEnd: 142,
    originalChapterStart: 132,
    originalChapterEnd: 142,
    resolvedAt: daysAgo(1),
    resolvedBy: 'Maki',
    queuedCount: 11,
    created: daysAgo(2),
  }),
  request(7, {
    status: 'Approved',
    seriesId: 11,
    title: 'Neon Shrine Maiden',
    coverUrl: cover(11),
    year: 2022,
    resolvedAt: daysAgo(3),
    resolvedBy: 'Maki',
    queuedCount: 0,
    resolutionNote: 'Already had most of it, queued the rest',
    created: daysAgo(4),
  }),
  request(8, {
    status: 'Rejected',
    title: 'Paper Crane Cartel',
    coverUrl: cover('Paper Crane Cartel'),
    year: 2019,
    requestedBy: 'Theo',
    userId: 3,
    resolvedAt: daysAgo(5),
    resolvedBy: 'Maki',
    resolutionNote: 'Not available from any enabled source',
    created: daysAgo(6),
  }),
]

export const allRequests: SeriesRequest[] = [...pendingRequests, processingRequest, ...resolvedRequests]

/** What a non-admin sees: only their own requests, whatever their status. */
export const ownRequests: SeriesRequest[] = allRequests.filter((r) => r.userId === 2)

export const requestsFor = (status: string | null, source: SeriesRequest[] = allRequests) =>
  status === 'pending'
    ? source.filter((r) => r.status === 'Pending' || r.status === 'Processing')
    : status === 'resolved'
      ? source.filter((r) => r.status === 'Approved' || r.status === 'Rejected')
      : source

export const requestsHandlers = [
  http.get(api('/requests'), ({ request: req }) =>
    HttpResponse.json(requestsFor(new URL(req.url).searchParams.get('status'))),
  ),
  http.post(api('/requests'), async ({ request: req }) => {
    const body = (await req.json()) as { kind: SeriesRequest['kind']; chapterStart?: number | null; chapterEnd?: number | null; note?: string | null }
    return HttpResponse.json(
      request(50, {
        kind: body.kind,
        chapterStart: body.chapterStart ?? null,
        chapterEnd: body.chapterEnd ?? null,
        note: body.note ?? null,
      }),
    )
  }),
  http.put(api('/requests/:id'), async ({ request: req, params }) => {
    const body = (await req.json()) as { chapterStart: number | null; chapterEnd: number | null }
    const found = allRequests.find((r) => r.id === Number(params.id)) ?? pendingRequests[0]
    return HttpResponse.json({ ...found, ...body, editedAt: hoursAgo(0), editedBy: 'Maki' })
  }),
  http.post(api('/requests/:id/approve'), ({ params }) => {
    const found = allRequests.find((r) => r.id === Number(params.id)) ?? pendingRequests[0]
    return HttpResponse.json({ ...found, status: 'Approved', resolvedBy: 'Maki', queuedCount: 24 })
  }),
  http.post(api('/requests/:id/reject'), ({ params }) => {
    const found = allRequests.find((r) => r.id === Number(params.id)) ?? pendingRequests[0]
    return HttpResponse.json({ ...found, status: 'Rejected', resolvedBy: 'Maki' })
  }),
  http.delete(api('/requests/:id'), () => new HttpResponse(null, { status: 204 })),
]

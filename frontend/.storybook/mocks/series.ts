import { http, HttpResponse, type HttpHandler } from 'msw'
import type { ArchivePreview, SeriesDto } from '../../src/api/types'
import type { ChapterShape } from './series/fixtures'
import {
  chaptersFor,
  compareSnapshot,
  releases,
  relinkPlan,
  renamePlan,
  resolvedSourceUrl,
  sourceSearchResults,
  filesFor,
  mappingsFor,
  progressFor,
  providerDetail,
  relatedItems,
  rootFolders,
  scanStatusDone,
  scrobble,
  seriesDto,
  similarItems,
  sourceInfos,
  sourceOrderFor,
  tags,
  upgradeProfiles,
} from './series/fixtures'
import { api, failWith, hoursAgo } from './api'
import { catalogue } from './shared'
import { outlookFor } from './operations/upcoming'

export * from './series/fixtures'

const noContent = () => new HttpResponse(null, { status: 204 })
const bodyOf = async (request: Request) => ((await request.json().catch(() => ({}))) ?? {}) as Record<string, unknown>

/**
 * How each catalogue series looks on its own page. Ids outside the catalogue answer 404, so a story
 * for a missing series just visits `/series/999`.
 */
export const scenarios: Record<number, { shape: ChapterShape; overrides?: Partial<SeriesDto> }> = {
  1: { shape: { missing: [17, 18, 52], specials: [10.5, 60.5], upgradeable: 6 }, overrides: { upgradeProfileId: 1 } },
  2: { shape: { trailingMissing: 0 } },
  3: { shape: { missing: [12, 13, 14, 15, 16, 41, 42], trailingMissing: 8, unwanted: [20] } },
  4: { shape: { trailingMissing: 0 } },
  5: { shape: { trailingMissing: 0 } },
  6: { shape: { missing: [88, 89, 90], trailingMissing: 24, limit: 90 } },
  7: { shape: { trailingMissing: 0 } },
}

const idOf = (params: Record<string, string | readonly string[] | undefined>) => Number(params.id)
const known = (id: number) => catalogue.some((s) => s.id === id)
const shapeOf = (id: number): ChapterShape => scenarios[id]?.shape ?? {}

export const seriesFor = (id: number) => seriesDto(id, scenarios[id]?.overrides)

/** Series 1 as an archived series: unmonitored, files still on disk, archived by the signed-in admin. */
export const archivedSeriesDto = (id = 1) =>
  seriesDto(id, { ...scenarios[id]?.overrides, monitored: false, archivedAt: hoursAgo(24 * 12), archivedByUserId: 1 })

const notFound = () => failWith(404, 'Series not found', 'error.series.notFound')

/** What the archive and restore dialogs fetch when they open; nothing removed and nobody else reading by default. */
export const archivePreview = (preview: Partial<ArchivePreview> = {}): HttpHandler =>
  http.get(api('/series/:id/archive-preview'), () =>
    HttpResponse.json({ freeableBytes: 2_400_000_000, otherActiveReaders: 0, redownloadableChapters: 0, ...preview } satisfies ArchivePreview),
  )

/** What every write on the series page answers, so a play function can click through without a 501. */
const seriesMutations: HttpHandler[] = [
  archivePreview(),
  http.post(api('/series/:id/refresh'), () => HttpResponse.json({ newChapters: 3 })),
  http.post(api('/series/:id/refreshmetadata'), ({ params }) => HttpResponse.json(seriesFor(idOf(params)))),
  http.post(api('/series/:id/rescan'), () => HttpResponse.json({ newFiles: 2, relinked: 1, removed: 0, unrecognized: 1 })),
  http.post(api('/series/:id/move'), ({ params }) => HttpResponse.json(seriesFor(idOf(params)))),
  http.delete(api('/series/:id'), () => noContent()),
  http.post(api('/series/:id/monitormode'), async ({ request }) => HttpResponse.json({ mode: (await bodyOf(request)).mode })),
  http.post(api('/series/:id/readcleanup'), async ({ request }) => HttpResponse.json({ readFileCleanup: (await bodyOf(request)).mode })),
  http.post(api('/series/:id/upgradeprofile'), async ({ request }) =>
    HttpResponse.json({ upgradeProfileId: (await bodyOf(request)).upgradeProfileId ?? null }),
  ),
  http.post(api('/series/:id/incognito'), async ({ request }) => HttpResponse.json({ incognito: (await bodyOf(request)).mode })),
  http.post(api('/series/:id/notifications'), async ({ request }) =>
    HttpResponse.json({ notificationMode: (await bodyOf(request)).mode }),
  ),
  http.put(api('/series/:id/rating'), async ({ request }) => HttpResponse.json({ rating: (await bodyOf(request)).rating ?? null })),
  http.put(api('/series/:id/tags'), async ({ request }) => HttpResponse.json({ tagIds: (await bodyOf(request)).tagIds ?? [] })),
  http.post(api('/series/:id/searchmissing'), () => HttpResponse.json({ queued: 6 })),
  http.post(api('/series/:id/download/next'), async ({ request }) =>
    HttpResponse.json({ queued: Number((await bodyOf(request)).count ?? 1) }),
  ),
  http.delete(api('/series/:id/files'), async ({ request }) =>
    HttpResponse.json({ deleted: ((await request.json().catch(() => [])) as string[]).length, failed: 0 }),
  ),
  http.post(api('/series/:id/archive'), () => HttpResponse.json({ deleted: 0, failed: 0 })),
  http.post(api('/series/archive/bulk'), async ({ request }) => {
    const ids = ((await bodyOf(request)).seriesIds as number[] | undefined) ?? []
    return HttpResponse.json({
      results: ids.map((seriesId) => ({
        seriesId,
        ok: true,
        notFound: false,
        code: null,
        error: null,
        permanentDeleteAvailable: false,
        deleted: 0,
        failed: 0,
      })),
    })
  }),
  http.post(api('/series/:id/archive/removefiles'), () => HttpResponse.json({ deleted: 96, failed: 0 })),
  http.post(api('/series/:id/restore'), () => HttpResponse.json({ queued: 0, queueError: null })),
  http.put(api('/chapter/:id/wanted'), () => noContent()),
  http.put(api('/chapter/wanted'), async ({ request }) =>
    HttpResponse.json({ updated: ((await bodyOf(request)).chapterIds as number[] | undefined)?.length ?? 0 }),
  ),
  http.post(api('/chapter/:id/search'), () => HttpResponse.json({ queueItemId: 9001 })),
  http.post(api('/chapter/:id/download-from'), () => HttpResponse.json({ queueItemId: 9002 })),
  http.put(api('/chapter/link'), () => HttpResponse.json({ fileId: 501, linked: 1 })),
  http.put(api('/chapter/unlink'), async ({ request }) =>
    HttpResponse.json({ unlinked: ((await request.json().catch(() => [])) as number[]).length }),
  ),
  http.delete(api('/chapter'), async ({ request }) =>
    HttpResponse.json({ deleted: ((await request.json().catch(() => [])) as number[]).length }),
  ),
  http.post(api('/chapter/deletefiles'), async ({ request }) => {
    const ids = (await request.json().catch(() => [])) as number[]
    return HttpResponse.json({ deleted: ids.length, kept: 0, failed: 0, chaptersRemoved: 0 })
  }),
  http.post(api('/reader/chapters/state'), async ({ request }) =>
    HttpResponse.json({ updated: ((await bodyOf(request)).chapterIds as number[] | undefined)?.length ?? 0 }),
  ),
  http.post(api('/reader/chapter/:id/read'), () => noContent()),
  http.post(api('/reader/chapter/:id/unread'), () => noContent()),
  http.post(api('/upgrades/scan'), async ({ request }) => {
    // A chapter-scoped scan ("Upgrade now") answers with what it considered; a series scan just starts.
    if ((await bodyOf(request)).chapterId == null) return HttpResponse.json({ started: true })
    return HttpResponse.json({
      seriesScanned: 1,
      chaptersChecked: 1,
      candidatesProbed: 1,
      enqueued: 1,
      skipped: {},
      queuedFromMappingId: 11,
      candidates: [
        { mappingId: 11, sourceName: 'mangadex', sourceChapterId: 'md-1', reason: 'enqueued', probed: true, pageCount: 26, medianWidth: 1600, score: 94 },
      ],
    })
  }),
  http.post(api('/upgrades/volume-search'), () => HttpResponse.json({ started: true })),
  http.post(api('/chapter-files/:id/trusted'), async ({ request }) => HttpResponse.json({ trusted: Boolean((await bodyOf(request)).trusted) })),
  http.post(api('/upgrades/proposals/:id/grab'), () => HttpResponse.json({ queueItemId: 9100 })),
  http.post(api('/upgrades/proposals/:id/dismiss'), () => noContent()),
  http.post(api('/series/:id/anime-resume/apply'), () => HttpResponse.json({ updated: 38, resumeChapterId: 1390, coveredTo: 38 })),
  http.post(api('/series/:id/anime-resume/dismiss'), () => noContent()),
  http.delete(api('/series/:id/anime-resume/dismiss'), () => noContent()),
  http.put(api('/series/:id/metadata'), () => HttpResponse.json({ lockedFields: ['title'], refreshed: true, coverRestored: false })),
  http.post(api('/series/:id/metadata/reset'), () => HttpResponse.json({ lockedFields: [], refreshed: true, coverRestored: true })),
  http.post(api('/mediacover/:id/cover'), () => HttpResponse.json({ coverUrl: '/mock/cover/1.svg', lockedFields: ['cover'] })),
  http.post(api('/sourcemapping'), async ({ request }) =>
    HttpResponse.json({ ...mappingsFor(1)[0], ...(await bodyOf(request)), id: 99, enabled: true, origin: 'Manual' }),
  ),
  http.put(api('/sourcemapping/priority'), () => HttpResponse.json([])),
  http.put(api('/sourcemapping/ordermode'), () => noContent()),
  http.put(api('/sourcemapping/:id'), () => HttpResponse.json(mappingsFor(1)[0])),
  http.post(api('/sourcemapping/scout'), () =>
    HttpResponse.json({
      running: true,
      probes: 6,
      done: 0,
      measured: 0,
      startedAtUtc: hoursAgo(0),
      finishedAtUtc: null,
      chapters: ['Ch. 12'],
      sources: [],
    }),
  ),
  http.post(api('/sourcemapping/automatch'), () => HttpResponse.json({ queued: 1 })),
  http.post(api('/sourcemapping/snapshots/refresh'), () => HttpResponse.json({ newChapters: 0 })),
  http.delete(api('/sourcemapping/:id'), () => noContent()),
  http.post(api('/sourcemapping/:id/remove'), () =>
    HttpResponse.json({
      removedChapters: 0,
      retainedChapters: 142,
      detachedFiles: 0,
      deletedFiles: 0,
      failedFileDeletions: 0,
      failedFileDeletionPaths: [],
    }),
  ),
  http.post(api('/chapter/redownload'), () => HttpResponse.json({ queued: 12, unavailable: 2 })),
  http.post(api('/scrobble/sync'), () => HttpResponse.json({ message: 'Sync started' })),
  http.post(api('/scrobble/match'), () => HttpResponse.json({ message: 'Linked to the tracker entry' })),
  http.post(api('/scrobble/ignore'), () => HttpResponse.json({ message: 'This series will be ignored for that tracker' })),
  http.post(api('/tags'), async ({ request }) => {
    const { label, color } = (await bodyOf(request)) as { label: string; color: string }
    return HttpResponse.json({ id: 99, label, color, seriesCount: 0 })
  }),
]

/** Endpoints only the series page and its modals call, registered for every story. */
export const seriesHandlers: HttpHandler[] = [
  http.get(api('/series/:id'), ({ params }) => (known(idOf(params)) ? HttpResponse.json(seriesFor(idOf(params))) : notFound())),
  http.get(api('/chapter'), ({ request }) => {
    const id = Number(new URL(request.url).searchParams.get('seriesId'))
    return known(id) ? HttpResponse.json(chaptersFor(id, shapeOf(id))) : HttpResponse.json([])
  }),
  http.get(api('/series/:id/files/summary'), ({ params }) => {
    const id = idOf(params)
    if (!known(id)) return notFound()
    const files = filesFor(id, chaptersFor(id, shapeOf(id)))
    return HttpResponse.json({ count: files.length, unlinkedOnDisk: files.filter((f) => f.onDisk && f.status !== 'linked').length })
  }),
  http.get(api('/series/:id/files'), ({ params }) => {
    const id = idOf(params)
    return known(id) ? HttpResponse.json(filesFor(id, chaptersFor(id, shapeOf(id)))) : notFound()
  }),
  http.get(api('/series/:id/related'), ({ params }) => HttpResponse.json(relatedItems(idOf(params)))),
  http.get(api('/series/:id/similar'), ({ params }) => HttpResponse.json(similarItems(idOf(params)))),
  http.get(api('/series/:id/scrobble'), () => HttpResponse.json(scrobble)),
  http.get(api('/series/:id/anime-resume'), () => noContent()),
  http.get(api('/series/:id/outlook'), ({ params }) => HttpResponse.json(outlookFor(idOf(params)))),
  http.get(api('/series/:id/metadata/history'), () => HttpResponse.json([])),
  http.get(api('/series/:id/rename/preview'), ({ params }) => HttpResponse.json(renamePlan(idOf(params)))),
  http.post(api('/series/:id/rename'), ({ params }) =>
    HttpResponse.json({ plan: renamePlan(idOf(params)), applied: true, error: null, warnings: [] }),
  ),
  http.post(api('/series/:id/relink/plan'), async ({ params, request }) => {
    const excluded = ((await bodyOf(request)).excludedPaths as string[] | undefined) ?? []
    const plan = relinkPlan(idOf(params))
    return HttpResponse.json({ ...plan, files: plan.files.map((f) => ({ ...f, excluded: excluded.includes(f.relativePath) })) })
  }),
  http.post(api('/series/:id/relink'), () =>
    HttpResponse.json({ moved: 6, superseded: 1, deleted: 0, kept: 1, failed: 0, freedBytes: 0 }),
  ),
  http.get(api('/release'), ({ request }) => {
    const query = new URL(request.url).searchParams.get('query')
    return HttpResponse.json({ query: query ?? 'Lanterns Over Kasumi', releases })
  }),
  http.post(api('/release/grab'), () => HttpResponse.json({ queueItemId: 9200 })),
  http.get(api('/sourcemapping/compare'), ({ request }) =>
    HttpResponse.json(compareSnapshot(Number(new URL(request.url).searchParams.get('seriesId')))),
  ),
  http.post(api('/sourcemapping/compare'), async ({ request }) =>
    HttpResponse.json(compareSnapshot(Number((await bodyOf(request)).seriesId))),
  ),
  http.get(api('/search/source'), ({ request }) =>
    HttpResponse.json(sourceSearchResults(new URL(request.url).searchParams.get('query') ?? '')),
  ),
  http.get(api('/search/resolvesource'), () => HttpResponse.json(resolvedSourceUrl)),
  http.get(api('/sourcemapping'), ({ request }) => {
    const id = Number(new URL(request.url).searchParams.get('seriesId'))
    return HttpResponse.json(known(id) ? mappingsFor(id) : [])
  }),
  http.get(api('/sourcemapping/quality'), ({ request }) => {
    const id = Number(new URL(request.url).searchParams.get('seriesId'))
    return HttpResponse.json(sourceOrderFor(known(id) ? id : 1))
  }),
  http.get(api('/upgrades/scan/status'), () => HttpResponse.json(scanStatusDone)),
  http.get(api('/reader/series/:id/progress'), ({ params }) => {
    const id = idOf(params)
    return HttpResponse.json(known(id) ? progressFor(id, chaptersFor(id, shapeOf(id))) : [])
  }),
  http.get(api('/reader/series/:id/continue'), ({ params }) => {
    const id = idOf(params)
    return HttpResponse.json(known(id) ? { chapterId: id * 1000 + 10 * (catalogue[id - 1].read + 1), page: 0 } : null)
  }),
  ...seriesMutations,
]

/**
 * Endpoints a whole series page needs that other areas also serve (the library list, the queue,
 * root folders). Not registered globally so a series story cannot change what the library or
 * Home stories see; page stories pass these as `parameters.msw`, ahead of any variant handlers.
 */
export const seriesPageHandlers: HttpHandler[] = [
  http.get(api('/series'), () => HttpResponse.json(catalogue.map((s) => seriesDto(s.id)))),
  http.get(api('/queue'), () => HttpResponse.json({ items: [], total: 0, page: 1, pageSize: 200 })),
  http.get(api('/reader/used'), () => HttpResponse.json({ used: true })),
  http.get(api('/rootfolder'), () => HttpResponse.json(rootFolders)),
  http.get(api('/recommendations/detail/:id'), ({ params }) => {
    const entry = catalogue.find((s) => String(s.mangaBakaId) === String(params.id))
    return entry ? HttpResponse.json(providerDetail(entry.id)) : failWith(404, 'Not found')
  }),
  http.get(api('/recommendations/reviews/:malId'), () => HttpResponse.json([])),
  http.get(api('/tags'), () => HttpResponse.json(tags)),
  http.get(api('/search/sources'), () => HttpResponse.json(sourceInfos)),
  http.get(api('/upgrade-profiles'), () => HttpResponse.json(upgradeProfiles)),
  http.get(api('/upgrades/proposals'), () => HttpResponse.json([])),
  http.get(api('/settings/sources/priority'), () =>
    HttpResponse.json({ order: sourceInfos.map((s) => s.name), disabled: ['shonenplaza'] }),
  ),
  http.get(api('/recyclebin/retention'), () => HttpResponse.json({ days: 14 })),
]

/** `parameters.msw` for a series page story: its variants first, then the shared page endpoints. */
export const seriesMsw = (...variants: HttpHandler[]): HttpHandler[] => [...variants, ...seriesPageHandlers]

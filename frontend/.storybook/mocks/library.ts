import { http, HttpResponse, type HttpHandler } from 'msw'
import type { LibraryFilterSpec, SavedFilterDto, TagDto } from '../../src/api/types'
import type { SeriesBulkStatus } from '../../src/api/seriesBulk'
import { api } from './api'
import { libraryTags, librarySeries, librarySeriesWithArchived, rootFolders, savedFilters, sources, upgradeProfiles } from './library/fixtures'
import { chaptersFor, progressFor } from './series/fixtures'

export * from './library/fixtures'

const noContent = () => new HttpResponse(null, { status: 204 })

/**
 * The server-side bulk jobs: queueing answers an operation id, and its first status poll answers
 * finished. `failure` names the series that fail and the server's error for them.
 */
export function bulkJobHandlers(failure: (seriesId: number) => string | null = () => null): HttpHandler[] {
  const jobs = new Map<string, { action: string; ids: number[] }>()
  return [
    http.post(api('/series/monitormode/bulk'), async ({ request }) => {
      const body = (await request.json()) as { seriesIds: number[] }
      return HttpResponse.json({ updated: body.seriesIds.length, skipped: [] })
    }),
    http.get(api('/series/bulk/:operationId'), ({ params }) => {
      const id = String(params.operationId)
      const job = jobs.get(id)
      if (!job) return new HttpResponse(null, { status: 404 })
      const failures = job.ids.flatMap((seriesId) => {
        const error = failure(seriesId)
        return error ? [{ seriesId, title: null, error }] : []
      })
      return HttpResponse.json({
        operationId: id,
        action: job.action,
        state: 'finished',
        total: job.ids.length,
        done: job.ids.length,
        succeeded: job.ids.length - failures.length,
        failed: failures.length,
        failures,
      } satisfies SeriesBulkStatus)
    }),
    http.post(api('/series/bulk/:operationId/cancel'), noContent),
    ...['refresh', 'rescan', 'refreshmetadata', 'searchmissing', 'rename', 'move'].map((action) =>
      http.post(api(`/series/${action}/bulk`), async ({ request }) => {
        const body = (await request.json()) as { seriesIds: number[] }
        const operationId = `op-${jobs.size + 1}`
        jobs.set(operationId, { action, ids: body.seriesIds })
        return HttpResponse.json({ operationId, total: body.seriesIds.length, skipped: [] }, { status: 202 })
      }),
    ),
  ]
}

/**
 * Library page endpoints. Other areas also answer `/series`, `/rootfolder` and `/reader/used` and
 * come earlier in `defaultHandlers`, so library stories pass this array through `parameters.msw`
 * (it then wins) instead of relying on the defaults.
 */
export const libraryHandlers: HttpHandler[] = [
  ...bulkJobHandlers(),
  http.get(api('/series'), () => HttpResponse.json(librarySeries)),
  http.get(api('/rootfolder'), () => HttpResponse.json(rootFolders)),
  http.get(api('/tags'), () => HttpResponse.json(libraryTags)),
  http.get(api('/library/filters'), () => HttpResponse.json(savedFilters)),
  http.get(api('/search/sources'), () => HttpResponse.json(sources)),
  http.get(api('/upgrade-profiles'), () => HttpResponse.json(upgradeProfiles)),
  http.get(api('/reader/used'), () => HttpResponse.json({ used: true })),
  http.get(api('/recyclebin/retention'), () => HttpResponse.json({ days: 14 })),

  http.post(api('/library/filters'), async ({ request }) => {
    const body = (await request.json()) as { name: string; spec: LibraryFilterSpec }
    return HttpResponse.json({ id: 99, name: body.name, spec: body.spec, sortOrder: 3, isDefault: false } satisfies SavedFilterDto)
  }),
  http.put(api('/library/filters/:id'), async ({ params, request }) => {
    const body = (await request.json()) as { name: string; spec: LibraryFilterSpec }
    return HttpResponse.json({ id: Number(params.id), name: body.name, spec: body.spec, sortOrder: 0, isDefault: false } satisfies SavedFilterDto)
  }),
  http.put(api('/library/filters/:id/default'), async ({ params, request }) => {
    const body = (await request.json()) as { isDefault: boolean }
    const current = savedFilters.find((f) => f.id === Number(params.id)) ?? savedFilters[0]
    return HttpResponse.json({ ...current, isDefault: body.isDefault } satisfies SavedFilterDto)
  }),
  http.put(api('/library/filters/order'), async ({ request }) => {
    const body = (await request.json()) as { ids: number[] }
    return HttpResponse.json(body.ids.map((id, i) => ({ ...(savedFilters.find((f) => f.id === id) ?? savedFilters[0]), sortOrder: i })))
  }),
  http.post(api('/library/filters/restore'), async ({ request }) =>
    HttpResponse.json((await request.json()) as SavedFilterDto),
  ),
  http.delete(api('/library/filters/:id'), noContent),

  http.post(api('/tags'), async ({ request }) => {
    const body = (await request.json()) as { label: string; color?: string }
    return HttpResponse.json({ id: 50, label: body.label, color: body.color ?? 'blue', seriesCount: 0 } satisfies TagDto)
  }),
  http.put(api('/tags/:id'), async ({ params, request }) => {
    const body = (await request.json()) as { label?: string; color?: string }
    const current = libraryTags.find((t) => t.id === Number(params.id)) ?? libraryTags[0]
    return HttpResponse.json({ ...current, ...body } satisfies TagDto)
  }),
  http.delete(api('/tags/:id'), noContent),
  http.post(api('/tags/bulk'), async ({ request }) => {
    const body = (await request.json()) as { seriesIds: number[] }
    return HttpResponse.json({ updated: body.seriesIds.length })
  }),

  http.post(api('/sourcemapping/automatch'), async ({ request }) => {
    const body = (await request.json()) as { seriesIds: number[] }
    return HttpResponse.json({ queued: body.seriesIds.length })
  }),
  http.post(api('/series/notifications/bulk'), async ({ request }) => {
    const body = (await request.json()) as { seriesIds: number[] }
    return HttpResponse.json({ updated: body.seriesIds.length })
  }),
  http.post(api('/series/reading-status/bulk'), async ({ request }) => {
    const body = (await request.json()) as { seriesIds: number[] }
    return HttpResponse.json({ updated: body.seriesIds.length })
  }),
  http.post(api('/series/upgradeprofile/bulk'), async ({ request }) => {
    const body = (await request.json()) as { seriesIds: number[] }
    return HttpResponse.json({ updated: body.seriesIds.length })
  }),

  http.post(api('/series/:id/searchmissing'), () => HttpResponse.json({ queued: 3 })),
  http.post(api('/series/:id/refresh'), () => HttpResponse.json({})),
  http.post(api('/series/:id/refreshmetadata'), () => HttpResponse.json({})),
  http.post(api('/series/:id/updatecomicinfo'), () => HttpResponse.json({})),
  http.post(api('/series/:id/monitormode'), () => HttpResponse.json({})),
  http.post(api('/series/:id/move'), () => HttpResponse.json({})),
  http.delete(api('/series/:id'), noContent),
]

/**
 * The Archived view: the library list with three series archived, and one of them (id 4) read with
 * chapters 3 and 5 left behind, so its card shows a skipped badge. The other archived series answer
 * the page-wide chapter and progress mocks.
 */
export const archivedLibraryHandlers: HttpHandler[] = [
  http.get(api('/series'), () => HttpResponse.json(librarySeriesWithArchived)),
  http.get(api('/series/12/archive-preview'), () =>
    HttpResponse.json({ freeableBytes: 0, otherActiveReaders: 0, redownloadableChapters: 75 }),
  ),
  http.get(api('/series/:id/archive-preview'), () =>
    HttpResponse.json({ freeableBytes: 3_400_000_000, otherActiveReaders: 0, redownloadableChapters: 0 }),
  ),
  http.get(api('/reader/series/4/progress'), () => {
    const gap = new Set([4030, 4050])
    return HttpResponse.json(progressFor(4, chaptersFor(4)).filter((p) => !gap.has(p.chapterId)))
  }),
  ...libraryHandlers,
]

/**
 * Clears what the Library page remembers about the previous story (session-stored filters, the
 * localStorage view and density) and seeds the given values. Call it from a story's `beforeEach`.
 */
export function seedLibraryPrefs(options: { view?: 'grid' | 'list' | 'table'; density?: 'compact' | 'default' | 'comfortable'; page?: Record<string, unknown> } = {}) {
  try {
    for (const key of Object.keys(sessionStorage)) if (key.startsWith('maki-page:')) sessionStorage.removeItem(key)
    localStorage.removeItem('library-view')
    localStorage.removeItem('library-density')
    localStorage.removeItem('library-table-columns')
    localStorage.removeItem('library-collapsed-groups')
    if (options.view) localStorage.setItem('library-view', options.view)
    if (options.density) localStorage.setItem('library-density', options.density)
    for (const [key, value] of Object.entries(options.page ?? {})) {
      sessionStorage.setItem(`maki-page:library:${key}`, JSON.stringify({ v: value }))
    }
  } catch {
    // Storage blocked: the page falls back to its defaults.
  }
}

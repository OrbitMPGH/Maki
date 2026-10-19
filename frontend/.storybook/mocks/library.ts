import { http, HttpResponse, type HttpHandler } from 'msw'
import type { LibraryFilterSpec, SavedFilterDto, TagDto } from '../../src/api/types'
import { api } from './api'
import { libraryTags, librarySeries, rootFolders, savedFilters, sources, upgradeProfiles } from './library/fixtures'

export * from './library/fixtures'

const noContent = () => new HttpResponse(null, { status: 204 })

/**
 * Library page endpoints. Other areas also answer `/series`, `/rootfolder` and `/reader/used` and
 * come earlier in `defaultHandlers`, so library stories pass this array through `parameters.msw`
 * (it then wins) instead of relying on the defaults.
 */
export const libraryHandlers: HttpHandler[] = [
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
    return HttpResponse.json({ id: 99, name: body.name, spec: body.spec, sortOrder: 3 } satisfies SavedFilterDto)
  }),
  http.put(api('/library/filters/:id'), async ({ params, request }) => {
    const body = (await request.json()) as { name: string; spec: LibraryFilterSpec }
    return HttpResponse.json({ id: Number(params.id), name: body.name, spec: body.spec, sortOrder: 0 } satisfies SavedFilterDto)
  }),
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
 * Clears what the Library page remembers about the previous story (session-stored filters, the
 * localStorage view and density) and seeds the given values. Call it from a story's `beforeEach`.
 */
export function seedLibraryPrefs(options: { view?: 'grid' | 'list'; density?: 'compact' | 'default' | 'comfortable'; page?: Record<string, unknown> } = {}) {
  try {
    for (const key of Object.keys(sessionStorage)) if (key.startsWith('maki-page:')) sessionStorage.removeItem(key)
    localStorage.removeItem('library-view')
    localStorage.removeItem('library-density')
    if (options.view) localStorage.setItem('library-view', options.view)
    if (options.density) localStorage.setItem('library-density', options.density)
    for (const [key, value] of Object.entries(options.page ?? {})) {
      sessionStorage.setItem(`maki-page:library:${key}`, JSON.stringify({ v: value }))
    }
  } catch {
    // Storage blocked: the page falls back to its defaults.
  }
}

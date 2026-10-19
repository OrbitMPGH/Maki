import { http, HttpResponse, type HttpHandler } from 'msw'
import type { BookmarkDto, ResolvedReaderPrefs } from '../../src/api/reader'
import type { ReaderPrefs } from '../../src/pages/reader/prefs'
import type {
  DiscoverPreset,
  DiscoverSearchResponse,
  HiddenContent,
  RecommendationFilters,
  SearchDefaults,
  TagOption,
} from '../../src/api/hooks'
import type { SeriesDto } from '../../src/api/types'
import { adminMe, api, failWith, hoursAgo } from './api'
import { catalogue } from './shared'
import {
  catalogueDetail,
  catalogueItems,
  chapters,
  librarySeries,
  librarySeriesList,
  librarySettings,
  readerSettings,
  readingProfiles,
  rootFolders,
  seriesRequest,
  upgradeProfiles,
} from './entry/fixtures'

/**
 * The reader. Every write succeeds and keeps nothing, so paging to a chapter's end in a story never
 * reaches a real backend or leaves a trace for the next story.
 */
const readerHandlers: HttpHandler[] = [
  http.get(api('/reader/chapter/:id'), ({ params }) => {
    const manifest = chapters[Number(params.id)]
    return manifest ? HttpResponse.json(manifest) : failWith(404, 'This chapter has no readable file.', 'error.reader.noFile')
  }),
  http.put(api('/reader/chapter/:id/progress'), () => new HttpResponse(null, { status: 204 })),
  http.get(api('/reader/chapter/:id/bookmarks'), ({ params }) =>
    HttpResponse.json([
      { id: 1, chapterId: Number(params.id), pageIndex: 3, createdAt: hoursAgo(5) },
      { id: 2, chapterId: Number(params.id), pageIndex: 9, createdAt: hoursAgo(5) },
    ] satisfies BookmarkDto[]),
  ),
  http.put(api('/reader/chapter/:id/bookmark/:page'), () => HttpResponse.json({ bookmarked: true })),
  http.post(api('/reader/chapter/:id/read'), () => new HttpResponse(null, { status: 204 })),
  http.post(api('/reader/chapter/:id/unread'), () => new HttpResponse(null, { status: 204 })),
  http.get(api('/reader/used'), () => HttpResponse.json({ used: true, kavita: false })),
  http.get(api('/readingprofiles'), () => HttpResponse.json(readingProfiles)),
  http.put(api('/readingprofiles/:id'), async ({ request, params }) => {
    const body = (await request.json()) as { name: string; prefs: ReaderPrefs; seriesTypes: string[] }
    return HttpResponse.json({ id: Number(params.id), ...body })
  }),
  http.put(api('/reader/series/:id/prefs'), async ({ request }) => {
    const { prefs } = (await request.json()) as { prefs: ReaderPrefs }
    return HttpResponse.json({
      prefs,
      source: 'Series',
      profileId: null,
      pinnedProfileId: null,
      autoProfileId: 1,
    } satisfies ResolvedReaderPrefs)
  }),
  http.put(api('/reader/series/:id/profile'), async ({ request }) => {
    const { profileId } = (await request.json()) as { profileId: number | null }
    const profile = readingProfiles.find((p) => p.id === profileId) ?? readingProfiles[0]
    return HttpResponse.json({
      prefs: profile.prefs,
      source: 'Profile',
      profileId: profile.id,
      pinnedProfileId: profileId,
      autoProfileId: 1,
    } satisfies ResolvedReaderPrefs)
  }),
  http.get(api('/settings/reader'), () => HttpResponse.json(readerSettings)),
  http.put(api('/settings/reader'), async ({ request }) =>
    HttpResponse.json({ ...readerSettings, ...((await request.json()) as object) }),
  ),
]

/** First-run account claim. */
const setupHandlers: HttpHandler[] = [http.post(api('/auth/setup'), () => HttpResponse.json(adminMe))]

export const entryHandlers: HttpHandler[] = [...readerHandlers, ...setupHandlers]

// ---------------------------------------------------------------------------------------------
// Add series
// ---------------------------------------------------------------------------------------------

const tagOptions: TagOption[] = [
  { name: 'Found Family', path: '', count: 812, hasSubtags: false },
  { name: 'Workplace', path: 'Setting', count: 640, hasSubtags: false },
  { name: 'School', path: 'Setting', count: 2210, hasSubtags: true },
]

function filtered(filters: RecommendationFilters | undefined) {
  const genres = filters?.genres ?? []
  return catalogueItems.filter((item) => {
    const entry = catalogue.find((c) => String(c.mangaBakaId) === item.providerId)
    return entry != null && genres.every((g) => entry.genres.includes(g))
  })
}

/**
 * Everything the catalogue browser and its detail card ask for, as an admin who can add series.
 * Not registered globally: Discover, Home and the series page serve some of these paths too, so an
 * Add series story passes this set as `parameters.msw` and keeps the variants it needs in front.
 */
export const addSeriesHandlers: HttpHandler[] = [
  http.get(api('/series'), () => HttpResponse.json(librarySeriesList)),
  http.post(api('/recommendations/discover/feed'), async ({ request }) => {
    const body = (await request.json()) as { filters?: RecommendationFilters; limit?: number }
    return HttpResponse.json(filtered(body.filters).slice(0, body.limit ?? 60))
  }),
  http.post(api('/recommendations/discover/search'), async ({ request }) => {
    const body = (await request.json()) as { query: string; filters?: RecommendationFilters; engine?: string }
    const needle = body.query.trim().toLowerCase()
    const items = filtered(body.filters).filter(
      (item) => item.title.toLowerCase().includes(needle) || (item.description ?? '').toLowerCase().includes(needle),
    )
    return HttpResponse.json({ mode: 'title', items, fallback: null } satisfies DiscoverSearchResponse)
  }),
  http.post(api('/recommendations/discover/count'), async ({ request }) => {
    const body = (await request.json()) as { filters?: RecommendationFilters }
    return HttpResponse.json({ count: filtered(body.filters).length })
  }),
  http.get(api('/recommendations/discover/searchdefaults'), () => HttpResponse.json({} satisfies SearchDefaults)),
  http.put(api('/recommendations/discover/searchdefaults'), async ({ request }) =>
    HttpResponse.json((await request.json()) as SearchDefaults),
  ),
  http.get(api('/recommendations/discover/hidden'), () => HttpResponse.json({ terms: [] } satisfies HiddenContent)),
  http.get(api('/discover/filters'), () => HttpResponse.json([] satisfies DiscoverPreset[])),
  http.get(api('/recommendations/tags'), () => HttpResponse.json(tagOptions)),
  http.get(api('/recommendations/detail/:id'), ({ params }) => {
    const entry = catalogue.find((c) => String(c.mangaBakaId) === params.id)
    return entry ? HttpResponse.json(catalogueDetail(entry)) : failWith(404, 'Not found')
  }),
  // No stored thumbs or hides for any title.
  http.get(api('/recommendations/feedback/:id'), () => failWith(404, 'No feedback', 'error.feedback.none')),
  http.get(api('/rootfolder'), () => HttpResponse.json(rootFolders)),
  http.get(api('/settings/library'), () => HttpResponse.json(librarySettings)),
  http.get(api('/upgrade-profiles'), () => HttpResponse.json(upgradeProfiles)),
  http.get(api('/requests'), () => HttpResponse.json([])),
  http.post(api('/requests'), async ({ request }) => {
    const body = (await request.json()) as { metadataProviderId: string; chapterStart: number | null; chapterEnd: number | null; note: string | null }
    const entry = catalogue.find((c) => String(c.mangaBakaId) === body.metadataProviderId) ?? catalogue[0]
    return HttpResponse.json(
      seriesRequest(entry, { chapterStart: body.chapterStart, chapterEnd: body.chapterEnd, note: body.note }),
    )
  }),
  http.post(api('/series'), async ({ request }) => {
    const body = (await request.json()) as { metadataProviderId: string }
    const entry = catalogue.find((c) => String(c.mangaBakaId) === body.metadataProviderId) ?? catalogue[2]
    return HttpResponse.json({ ...librarySeries(entry), warnings: [] } satisfies SeriesDto)
  }),
]

/** The browser's defaults for stories that start as a reader who can only request. */
export const requestOnlyHandlers: HttpHandler[] = [
  http.get(api('/requests'), () =>
    HttpResponse.json([
      seriesRequest(catalogue[8], { id: 40, status: 'Pending' }),
      seriesRequest(catalogue[10], { id: 41, status: 'Rejected', resolutionNote: 'Already coming through another title.' }),
    ]),
  ),
]

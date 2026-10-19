import { http, HttpResponse, type HttpHandler } from 'msw'
import type {
  CreatorRequest,
  DiscoverFeedRequest,
  DiscoverPreset,
  DiscoverSearchRequest,
  DiscoverSearchResponse,
  HiddenContent,
  LibrarySettings,
  RecommendationDefaults,
  SearchDefaults,
} from '../../src/api/hooks'
import type { CreatorProfile } from '../../src/api/hooks'
import type { FeedbackMutation, FeedbackState } from '../../src/api/recommendationFeedback'
import type { FollowedCreators } from '../../src/api/following'
import type { SeriesRequest, CreateSeriesRequestBody } from '../../src/api/requests'
import type { MetadataSearchResult, RootFolder, SeriesDto } from '../../src/api/types'
import { DEFAULT_PREFS } from '../../src/pages/reader/prefs'
import { api, cover, failWith, hoursAgo } from './api'
import { librarySeries } from './home/fixtures'
import {
  animeSignals,
  catalogueItem,
  catalogueRails,
  cohortRail,
  creatorWorks,
  detailFor,
  discoverRails,
  emptyRecommendationDefaults,
  feedbackLab,
  feedbackStates,
  followedCreators,
  followingItems,
  genreRails,
  hiddenContent,
  indexStatusReady,
  presets,
  previewReady,
  readingBehaviour,
  recentRail,
  recommendationsPage,
  reviews,
  searchDefaults,
  seedTitles,
  sideInterestRails,
  signalOverrides,
  tagOptions,
  tasteInsights,
  tasteProfile,
} from './discover/fixtures'

export * from './discover/fixtures'

const noContent = () => new HttpResponse(null, { status: 204 })

const everyRail = [...discoverRails, ...genreRails]

function feedItems(body: DiscoverFeedRequest) {
  if (body.filters?.credits?.length) return followingItems
  const match = everyRail.find((r) => r.feed === body.feed && (r.genre ?? null) === (body.genre ?? null))
  const pool = match?.items ?? catalogueRails[0].items
  const sorted = body.sort === 'newest' ? [...pool].sort((a, b) => (b.year ?? 0) - (a.year ?? 0)) : pool
  const start = body.offset ?? 0
  return sorted.slice(start, start + (body.limit ?? 40))
}

function searchItems(body: DiscoverSearchRequest): DiscoverSearchResponse {
  const words = body.query.toLowerCase().split(/\s+/).filter(Boolean)
  const hits = seedTitles
    .map((s, n) => ({ s, n }))
    .filter(({ s }) => words.some((w) => `${s.title} ${s.blurb} ${s.author}`.toLowerCase().includes(w)))
    .map(({ n }) => catalogueItem(n))
  const author = seedTitles.find((s) => body.query.toLowerCase().includes(s.author.toLowerCase().split(' ')[0]))
  return {
    mode: body.engine === 'title' ? 'title' : 'semantic',
    items: hits,
    credits: author
      ? [{ name: author.author, roles: ['author'], workCount: seedTitles.filter((s) => s.author === author.author).length }]
      : null,
  }
}

const feedbackEffect: Record<string, string> = {
  like: 'positive-title',
  dislike: 'negative-taste',
  hide: 'negative-title',
  dismiss: 'temporary',
  'mark-exposed': 'neutral-exposure',
}

function mutated(id: number, action: string, medium?: string): FeedbackMutation {
  const state: FeedbackState = {
    mangaBakaId: id,
    suppression: action === 'hide' || action === 'dislike' ? 'hidden' : action === 'dismiss' ? 'dismissed' : 'none',
    exposure: action === 'mark-exposed' ? [medium ?? 'manga'] : [],
    dismissedUntilUtc: null,
    revision: 2,
    title: null,
    sentiment: action === 'like' ? 'liked' : action === 'dislike' ? 'disliked' : 'none',
    coverUrl: null,
    genres: null,
    updatedAtUtc: hoursAgo(0),
  }
  return {
    changed: true,
    eventId: 900 + id,
    state,
    feedbackRevision: 8,
    signalRevision: 3,
    queueEffect: state.suppression === 'none' ? 'unchanged' : 'suppressed',
    tasteEffect: 'unchanged',
    feedbackEffect: feedbackEffect[action] ?? 'neutral',
  }
}

/**
 * Everything under `/recommendations`, the Discover filter presets and the series preview. Nothing
 * outside Discover, Add series, the creator page and Settings calls these, so they are global.
 */
export const discoverHandlers: HttpHandler[] = [
  http.get(api('/recommendations/discover'), () => HttpResponse.json(discoverRails)),
  http.get(api('/recommendations/discover/recent'), () => HttpResponse.json(recentRail)),
  http.get(api('/recommendations/discover/side-interests'), () => HttpResponse.json(sideInterestRails)),
  http.get(api('/recommendations/discover/genres'), () => HttpResponse.json(genreRails)),
  http.post(api('/recommendations/discover/cohort'), () => HttpResponse.json(cohortRail)),
  http.post(api('/recommendations/discover/feed'), async ({ request }) =>
    HttpResponse.json(feedItems((await request.json()) as DiscoverFeedRequest)),
  ),
  http.post(api('/recommendations/discover/search'), async ({ request }) =>
    HttpResponse.json(searchItems((await request.json()) as DiscoverSearchRequest)),
  ),
  http.post(api('/recommendations/discover/count'), () => HttpResponse.json({ count: 128 })),
  http.get(api('/recommendations/discover/following'), () => HttpResponse.json(followedCreators)),
  http.put(api('/recommendations/discover/following'), async ({ request }) =>
    HttpResponse.json((await request.json()) as FollowedCreators),
  ),
  http.get(api('/recommendations/discover/hidden'), () => HttpResponse.json(hiddenContent)),
  http.put(api('/recommendations/discover/hidden'), async ({ request }) =>
    HttpResponse.json((await request.json()) as HiddenContent),
  ),
  http.get(api('/recommendations/discover/searchdefaults'), () => HttpResponse.json(searchDefaults)),
  http.put(api('/recommendations/discover/searchdefaults'), async ({ request }) =>
    HttpResponse.json((await request.json()) as SearchDefaults),
  ),
  http.get(api('/recommendations/defaults'), () => HttpResponse.json(emptyRecommendationDefaults)),
  http.put(api('/recommendations/defaults'), async ({ request }) =>
    HttpResponse.json((await request.json()) as RecommendationDefaults),
  ),
  http.post(api('/recommendations'), async ({ request }) => {
    const body = (await request.json()) as { page?: number }
    return HttpResponse.json(recommendationsPage(body.page ?? 0))
  }),
  http.post(api('/recommendations/creator'), async ({ request }) => {
    const body = (await request.json()) as CreatorRequest
    if (body.name === 'Nobody Atall') return failWith(404, 'No such creator', 'error.creator.notFound')
    const works = creatorWorks()
    return HttpResponse.json({
      name: body.name,
      roles: body.role ? [body.role] : ['author', 'artist'],
      workCount: works.length,
      items: works.slice(body.offset ?? 0, (body.offset ?? 0) + (body.limit ?? 60)),
    } satisfies CreatorProfile)
  }),
  http.get(api('/recommendations/credits'), ({ request }) => {
    const q = (new URL(request.url).searchParams.get('q') ?? '').toLowerCase()
    const names = [...new Set(seedTitles.flatMap((s) => [s.author, ...(s.artist ? [s.artist] : [])]))]
    return HttpResponse.json(
      names
        .filter((n) => n.toLowerCase().includes(q))
        .slice(0, 8)
        .map((name) => ({ name, roles: ['author'], workCount: seedTitles.filter((s) => s.author === name).length || 1 })),
    )
  }),
  http.get(api('/recommendations/tags'), () => HttpResponse.json(tagOptions)),
  http.get(api('/recommendations/detail/:id'), ({ params }) => HttpResponse.json(detailFor(String(params.id)))),
  http.get(api('/recommendations/reviews/:malId'), () => HttpResponse.json(reviews)),

  http.get(api('/recommendations/taste-profile'), () => HttpResponse.json(tasteProfile)),
  http.get(api('/recommendations/taste-insights'), () => HttpResponse.json(tasteInsights)),
  http.get(api('/recommendations/reading-behaviour'), () => HttpResponse.json(readingBehaviour)),

  http.get(api('/recommendations/feedback-lab'), () => HttpResponse.json(feedbackLab)),
  http.get(api('/recommendations/feedback/activity'), () => HttpResponse.json(feedbackLab.activity)),
  http.get(api('/recommendations/feedback/:id'), ({ params }) => {
    const state = feedbackStates.find((s) => s.mangaBakaId === Number(params.id))
    return state ? HttpResponse.json(state) : failWith(404, 'No feedback yet', 'error.feedback.notFound')
  }),
  http.get(api('/recommendations/feedback'), () =>
    HttpResponse.json({ items: feedbackStates, nextCursor: null, feedbackRevision: 7, signalRevision: 3 }),
  ),
  http.put(api('/recommendations/feedback/:id'), async ({ params, request }) => {
    const body = (await request.json()) as { action: string; medium?: string }
    return HttpResponse.json(mutated(Number(params.id), body.action, body.medium))
  }),
  http.post(api('/recommendations/feedback/:id/franchise'), () =>
    HttpResponse.json({ changed: 3, titles: [], feedbackRevision: 8 }),
  ),
  http.post(api('/recommendations/feedback/events/:eventId/undo'), ({ params }) =>
    HttpResponse.json(mutated(Number(params.eventId) - 900, 'clear-suppression')),
  ),
  http.get(api('/recommendations/signal-overrides'), () => HttpResponse.json(signalOverrides)),
  http.put(api('/recommendations/signal-overrides/:id'), async ({ params, request }) => {
    const body = (await request.json()) as { ignoreAsSeed: boolean }
    return HttpResponse.json({
      changed: true,
      state: { mangaBakaId: Number(params.id), ignoreAsSeed: body.ignoreAsSeed, revision: 2 },
      signalRevision: 4,
    })
  }),

  http.get(api('/recommendations/anime-signals'), () => HttpResponse.json(animeSignals)),
  http.put(api('/recommendations/anime-signals/settings'), async ({ request }) => {
    const body = (await request.json()) as { enabled: boolean; strength?: string }
    return HttpResponse.json({ enabled: body.enabled, strength: body.strength ?? 'balanced' })
  }),
  http.post(api('/recommendations/anime-signals/sync'), noContent),

  http.get(api('/discover/filters'), () => HttpResponse.json(presets)),
  http.post(api('/discover/filters'), async ({ request }) => {
    const body = (await request.json()) as { name: string; spec: SearchDefaults }
    return HttpResponse.json({ id: 50, sortOrder: 9, ...body } satisfies DiscoverPreset)
  }),
  http.put(api('/discover/filters/:id'), async ({ params, request }) => {
    const body = (await request.json()) as Partial<DiscoverPreset>
    const current = presets.find((p) => p.id === Number(params.id)) ?? presets[0]
    return HttpResponse.json({ ...current, ...body } satisfies DiscoverPreset)
  }),
  http.delete(api('/discover/filters/:id'), noContent),

  http.get(api('/settings/recommendations'), () => HttpResponse.json(indexStatusReady)),
  http.post(api('/settings/recommendations/build'), () => HttpResponse.json({ started: true })),
  http.post(api('/settings/recommendations/prebuilt/download'), () =>
    HttpResponse.json({ installed: true, reason: 'Installed', rowCount: 148_220 }),
  ),
  http.put(api('/settings/recommendations/prebuilt'), async ({ request }) =>
    HttpResponse.json((await request.json()) as { enabled: boolean }),
  ),
  http.put(api('/settings/recommendations/model'), async ({ request }) => {
    const body = (await request.json()) as { model: string }
    return HttpResponse.json({ model: body.model, switching: false, reason: '' })
  }),
  http.put(api('/settings/recommendations/fulldump'), async ({ request }) =>
    HttpResponse.json((await request.json()) as { useFullDump: boolean }),
  ),

  http.post(api('/preview/:id'), ({ params }) => HttpResponse.json(previewReady(String(params.id)))),
  http.get(api('/preview/:id'), ({ params }) => HttpResponse.json(previewReady(String(params.id)))),
  http.delete(api('/preview/:id'), noContent),
  http.get(api('/preview/:id/page/:n'), async ({ params }) => {
    const art = await fetch(`/mock/page/${params.id}/${Number(params.n) + 1}.svg`)
    return new HttpResponse(await art.text(), { headers: { 'Content-Type': 'image/svg+xml' } })
  }),
]

const rootFolders: RootFolder[] = [
  { id: 1, path: '/library/manga', freeSpace: 412_000_000_000, accessible: true },
  { id: 2, path: '/library/manhwa', freeSpace: 96_000_000_000, accessible: true },
]

const librarySettings: LibrarySettings = {
  writeComicInfo: true,
  folderNamingMode: 'rename',
  incognitoByRating: { safe: 'Off', suggestive: 'Off', erotica: 'Full', pornographic: 'Full' },
  newSeriesMonitorMode: 'All',
}

const pendingRequest = (body: CreateSeriesRequestBody): SeriesRequest => ({
  id: 700,
  kind: body.kind,
  status: 'Pending',
  userId: 2,
  requestedBy: 'reader',
  metadataProviderId: body.metadataProviderId ?? null,
  seriesId: null,
  title: 'The Salt Cartographer',
  coverUrl: cover('The Salt Cartographer'),
  year: 2023,
  chapterStart: body.chapterStart ?? null,
  chapterEnd: body.chapterEnd ?? null,
  note: body.note ?? null,
  created: hoursAgo(0),
  resolvedAt: null,
  resolvedBy: null,
  resolutionNote: null,
  queuedCount: null,
  editedAt: null,
  editedBy: null,
  originalChapterStart: null,
  originalChapterEnd: null,
})

/**
 * What Discover's cards and modal need from areas that own them: the library list (for the
 * in-library marker and seed strip), root folders, library defaults, the reader and the request
 * flow. Not registered globally so a Discover story cannot change what another area's stories see;
 * pass it as `parameters.msw`, variants first, via {@link discoverPage}.
 */
export const discoverPageHandlers: HttpHandler[] = [
  // The series mock registers its own detail and reviews globally, ahead of this file.
  http.get(api('/recommendations/detail/:id'), ({ params }) => HttpResponse.json(detailFor(String(params.id)))),
  http.get(api('/recommendations/reviews/:malId'), () => HttpResponse.json(reviews)),
  http.get(api('/series'), () => HttpResponse.json(librarySeries)),
  http.post(api('/series'), () =>
    HttpResponse.json({ ...librarySeries[0], id: 77, warnings: [] } satisfies SeriesDto & { warnings: string[] }),
  ),
  http.get(api('/rootfolder'), () => HttpResponse.json(rootFolders)),
  http.get(api('/settings/library'), () => HttpResponse.json(librarySettings)),
  http.get(api('/upgrade-profiles'), () => HttpResponse.json([])),
  http.get(api('/requests'), () => HttpResponse.json([])),
  http.post(api('/requests'), async ({ request }) =>
    HttpResponse.json(pendingRequest((await request.json()) as CreateSeriesRequestBody)),
  ),
  http.get(api('/search/metadata'), ({ request }) => {
    const q = (new URL(request.url).searchParams.get('query') ?? '').toLowerCase()
    return HttpResponse.json(
      seedTitles
        .map((s, n) => ({ s, n }))
        .filter(({ s }) => s.title.toLowerCase().includes(q))
        .slice(0, 6)
        .map(({ s, n }) => {
          const item = catalogueItem(n)
          return {
            providerId: item.providerId,
            title: s.title,
            coverUrl: item.coverUrl,
            year: s.year,
            status: s.status,
            description: s.blurb,
            totalChapters: s.chapters,
          } satisfies MetadataSearchResult
        }),
    )
  }),
  http.get(api('/settings/reader'), () => HttpResponse.json({ defaults: DEFAULT_PREFS, pushToKavita: false })),
  http.get(api('/readingprofiles'), () => HttpResponse.json([])),
]

/** A Discover page or component story's handlers: variants first so they win, then the shared set. */
export const discoverPage = (...variants: HttpHandler[]): HttpHandler[] => [...variants, ...discoverPageHandlers]

/**
 * Discover remembers filters, search text and paging in sessionStorage (`usePageState`), which
 * outlives a story. Run this in `beforeEach` so each story starts from the page's defaults.
 */
export function resetTabState() {
  try {
    for (const key of Object.keys(sessionStorage)) {
      if (key.startsWith('maki-page:') || key.startsWith('maki-scroll:')) sessionStorage.removeItem(key)
    }
    for (const key of Object.keys(localStorage)) {
      if (key.startsWith('discover') || key.endsWith('-search-mode')) localStorage.removeItem(key)
    }
  } catch {
    // Storage blocked: the page renders from its defaults anyway.
  }
}

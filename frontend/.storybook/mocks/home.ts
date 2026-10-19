import { http, HttpResponse, type HttpHandler } from 'msw'
import type { CustomRail, CustomRailSpec } from '../../src/api/customRails'
import type { HomeReadingResponse, UiSettings } from '../../src/api/hooks'
import { api } from './api'
import {
  animeResumeAllPage,
  animeResumePage,
  catalogueItem,
  continueReading,
  customRails,
  downloadingItems,
  failedItem,
  homeUi,
  jumpBackIn,
  librarySeries,
  popularPicks,
  progressSummary,
  queuePage,
  railItems,
  recentlyAdded,
  recommendationsResult,
} from './home/fixtures'

export * from './home/fixtures'

const noContent = () => new HttpResponse(null, { status: 204 })

/**
 * Home's own endpoints. Registered for every story, since nothing outside Home calls them. Pass
 * `parameters.msw` handlers to swap one for a loading, empty or failing variant.
 */
export const homeHandlers: HttpHandler[] = [
  http.get(api('/home/reading'), () =>
    HttpResponse.json({ continueReading, jumpBackIn } satisfies HomeReadingResponse),
  ),
  http.post(api('/home/reading/:seriesId/hide'), noContent),
  http.delete(api('/home/reading/:seriesId/hide'), noContent),
  http.get(api('/home/recently-added'), () => HttpResponse.json(recentlyAdded)),
  http.get(api('/home/from-anime'), ({ request }) => {
    const limit = Number(new URL(request.url).searchParams.get('limit') ?? 0)
    return HttpResponse.json(limit > 100 ? animeResumeAllPage : animeResumePage)
  }),
  http.get(api('/progress/summary'), () => HttpResponse.json(progressSummary)),

  http.get(api('/rails'), ({ request }) => {
    const placement = new URL(request.url).searchParams.get('placement')
    return HttpResponse.json(placement ? customRails.filter((r) => r.placement === placement) : customRails)
  }),
  http.post(api('/rails/count'), () => HttpResponse.json({ count: 128 })),
  http.get(api('/rails/:id/items'), ({ params, request }) => {
    const rail = customRails.find((r) => r.id === Number(params.id)) ?? customRails[0]
    const limit = Number(new URL(request.url).searchParams.get('limit') ?? 20)
    return HttpResponse.json(railItems(rail, limit))
  }),
  http.post(api('/rails'), async ({ request }) => {
    const body = (await request.json()) as { name: string; placement: CustomRail['placement']; spec: CustomRailSpec }
    return HttpResponse.json({ id: 90, sortOrder: 9, ...body } satisfies CustomRail)
  }),
  http.put(api('/rails/:id'), async ({ params, request }) => {
    const body = (await request.json()) as Partial<CustomRail>
    const current = customRails.find((r) => r.id === Number(params.id)) ?? customRails[0]
    return HttpResponse.json({ ...current, ...body } satisfies CustomRail)
  }),
  http.delete(api('/rails/:id'), noContent),
]

/**
 * The endpoints a whole Home page needs that other areas also serve (the library list, the queue,
 * the recommender, the layout setting). They are not registered globally so a Home story cannot
 * change what the library or discover stories see; a Home page story passes them as
 * `parameters.msw`, ahead of any variant handlers.
 */
export const homePageHandlers: HttpHandler[] = [
  http.get(api('/series'), () => HttpResponse.json(librarySeries)),
  http.get(api('/queue'), () => HttpResponse.json(queuePage([...downloadingItems.slice(0, 3), failedItem]))),
  http.get(api('/reader/used'), () => HttpResponse.json({ used: true })),
  http.get(api('/rootfolder'), () =>
    HttpResponse.json([{ id: 1, path: '/library', freeSpace: 412_000_000_000, accessible: true }]),
  ),
  http.get(api('/settings/ui'), () => HttpResponse.json(homeUi())),
  http.put(api('/settings/ui'), async ({ request }) => HttpResponse.json((await request.json()) as UiSettings)),
  http.post(api('/recommendations'), () => HttpResponse.json(recommendationsResult)),
  http.get(api('/recommendations/discover'), () =>
    HttpResponse.json([
      {
        key: 'popular',
        title: 'Popular',
        feed: 'Popular',
        genre: null,
        items: popularPicks,
        subtitle: null,
      },
    ]),
  ),
  http.get(api('/recommendations/discover/following'), () =>
    HttpResponse.json({
      creators: [
        { name: 'Aoi Mizuhara', role: 'author' },
        { name: 'Studio Lark', role: 'studio' },
      ],
    }),
  ),
  http.post(api('/recommendations/discover/feed'), () =>
    HttpResponse.json([catalogueItem(3), catalogueItem(5), catalogueItem(7), catalogueItem(9), catalogueItem(11)]),
  ),
  http.post(api('/recommendations/discover/count'), () => HttpResponse.json({ count: 128 })),
  http.get(api('/recommendations/tags'), () =>
    HttpResponse.json([
      { name: 'Found Family', path: '', count: 812, hasSubtags: false },
      { name: 'Workplace', path: 'Setting', count: 640, hasSubtags: false },
      { name: 'School', path: 'Setting', count: 2210, hasSubtags: true },
    ]),
  ),
]

/** A Home page story's handlers: variants first so they win, then the full set. */
export const homePage = (...variants: HttpHandler[]): HttpHandler[] => [...variants, ...homePageHandlers]

/** Home with a given layout and custom rails, for stories that vary the section list. */
export const homeLayoutHandlers = (options: Parameters<typeof homeUi>[0] = {}): HttpHandler[] => [
  http.get(api('/settings/ui'), () => HttpResponse.json(homeUi(options))),
]

import { http, HttpResponse, type HttpHandler } from 'msw'
import { sharedHandlers } from './shared'
import { homeHandlers } from './home'
import { libraryHandlers } from './library'
import { seriesHandlers } from './series'
import { discoverHandlers } from './discover'
import { settingsHandlers } from './settings'
import { operationsHandlers } from './operations'
import { insightsHandlers } from './insights'
import { shellHandlers } from './shell'
import { entryHandlers } from './entry'

/**
 * Requests no handler answered, collected so preview.tsx can fail the story: a page that silently
 * renders its error state because one endpoint was never mocked is the failure this catches.
 */
export const unmockedRequests: string[] = []

const catchAll: HttpHandler[] = [
  http.all('/api/*', ({ request }) => {
    const { pathname, search } = new URL(request.url)
    unmockedRequests.push(`${request.method} ${pathname}${search}`)
    return HttpResponse.json({ code: 'error.storybook.unmocked', error: `Not mocked: ${request.method} ${pathname}` }, { status: 501 })
  }),
  // Live events need a SignalR server. A refused negotiate logs an error and retries every few
  // seconds; one that never answers leaves the client quietly "connecting".
  http.post('/signalr/*', () => new Promise<never>(() => {})),
]

// First match wins, so shared shell endpoints come first and the catch-all last. A story overrides
// any of these with `parameters.msw` (or `msw.use` in its own beforeEach).
export const defaultHandlers: HttpHandler[] = [
  ...sharedHandlers,
  ...homeHandlers,
  ...libraryHandlers,
  ...seriesHandlers,
  ...discoverHandlers,
  ...settingsHandlers,
  ...operationsHandlers,
  ...insightsHandlers,
  ...shellHandlers,
  ...entryHandlers,
  ...catchAll,
]

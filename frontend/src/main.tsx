import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Notifications } from '@mantine/notifications'
import { notifications } from './lib/toast'
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { unstable_HistoryRouter as HistoryRouter } from 'react-router-dom'
import '@mantine/core/styles.css'
import '@mantine/notifications/styles.css'
// Bundled rather than linked from a font CDN: a self-hosted instance may have no internet, and
// the theme's font stack named a face without anything ever loading it. Onest carries Cyrillic, so
// the Russian locale sets in the same face as the rest.
import '@fontsource-variable/onest'
import '@fontsource-variable/bricolage-grotesque/opsz.css'
import './theme.css'
// Before App: decides whether the window or the shell's <main> scrolls.
import './lib/pageScroll'
import './lib/coverFallback'
import { appHistory, PageTransitionCommit } from './lib/pageTransition'
import { preloadingRoutes } from './lib/routePreload'
import { AppThemeProvider } from './theme-context'
import { AppI18nProvider } from './i18n-context'
import { i18n, loadLocale, resolveInitialLocale } from './i18n'
import App from './App.tsx'
import { ApiError, isOfflineUnavailable, UnauthorizedError } from './api/client'
import { ME_QUERY_KEY, type Me } from './api/auth'
import { syncSkeletonPulses } from './lib/skeletonSync'
import { pwaSupported, registerServiceWorker } from './lib/pwa/serviceWorker'

syncSkeletonPulses()

// A tab that loaded index.html before an upgrade asks for hashed chunk names that no longer exist
// on its next navigation; the import rejects and the page would go blank. Reload once so the new
// index.html and chunks come down together. The timestamp guard stops a reload loop when the
// chunks are genuinely unreachable; the route error boundary then shows a message instead.
window.addEventListener('vite:preloadError', (event) => {
  if (preloadingRoutes()) return
  const key = 'maki.chunk-reload'
  let last = 0
  try {
    last = Number(sessionStorage.getItem(key) ?? 0)
  } catch {
    // Storage can be unavailable in private windows; a missing guard only risks one extra reload.
  }
  if (Date.now() - last < 30_000) return
  event.preventDefault()
  try {
    sessionStorage.setItem(key, String(Date.now()))
  } catch {
    // Same as above.
  }
  window.location.reload()
})

/**
 * One place that reports failures, so no call site can swallow one by forgetting a handler,
 * which is exactly how the series monitor toggle ended up reverting silently. Call sites only
 * need their own `onError` for extra work (resetting local state); the toast is automatic.
 *
 * A call site never toasts the server's own message, that is this handler's job. `meta.errorMessage`
 * overrides the text; `meta.silent` opts out entirely for flows that show failure inline (bulk
 * actions with per-row results) or word it themselves in an `onError`. `meta.inlineNotFound` drops
 * only a 404, for pages that render their own not-found state.
 */
const recentErrorToasts = new Map<string, number>()
const ERROR_TOAST_DEDUPE_MS = 3000

function reportError(error: unknown, meta?: Record<string, unknown>) {
  if (meta?.silent) return
  if (meta?.inlineNotFound && error instanceof ApiError && error.status === 404) return
  // A call site's own wording describes a server failure; "not available offline" is the truer one.
  const message =
    typeof meta?.errorMessage === 'string' && !isOfflineUnavailable(error)
      ? meta.errorMessage
      : error instanceof Error
        ? error.message
        : String(error)
  // A page mounting several queries while the API is down fails them all with the same text at
  // once; one toast says it.
  const now = Date.now()
  const last = recentErrorToasts.get(message)
  if (last !== undefined && now - last < ERROR_TOAST_DEDUPE_MS) return
  recentErrorToasts.set(message, now)
  notifications.show({ message, color: 'var(--danger)' })
}

function isFinalClientError(error: unknown): boolean {
  if (error instanceof UnauthorizedError) return true
  // Nothing saved on this device; asking the worker again gives the same answer.
  if (isOfflineUnavailable(error)) return true
  if (!(error instanceof ApiError)) return false
  return error.status >= 400 && error.status < 500 && error.status !== 408 && error.status !== 429
}

// With a service worker the network is not the only source: queries must reach it while the browser
// says it is offline ('online' mode would pause them), and a mutation should fail on the worker's
// 503 at once rather than wait and fire whenever the connection returns.
const withWorker = pwaSupported()

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // A 4xx (not-found, forbidden, validation) won't change on a second try; retrying only delays
      // the page's own state. Timeouts and rate limits are the 4xx worth another go.
      retry: (failureCount, error) => failureCount < 1 && !isFinalClientError(error),
      refetchOnWindowFocus: false,
      ...(withWorker && { networkMode: 'offlineFirst' as const }),
    },
    mutations: withWorker ? { networkMode: 'always' } : {},
  },
  // Background refetches fail silently by design: a toast on every poll while the API is down
  // would bury the app. Only surface a query error when there's no data to fall back on.
  // Offline, every read the device holds no copy of fails the same way, and polls repeat it; the
  // offline banner already says so, and the page shows its own empty state. Actions still toast.
  queryCache: new QueryCache({
    onError: (error, query) => {
      if (query.state.data === undefined && !isOfflineUnavailable(error)) reportError(error, query.meta)
    },
  }),
  mutationCache: new MutationCache({
    onError: (error, _vars, _ctx, mutation) => reportError(error, mutation.meta),
  }),
})

if (import.meta.env.PROD) {
  registerServiceWorker(() => {
    const me = queryClient.getQueryData<Me | null>(ME_QUERY_KEY)
    return me === undefined ? undefined : (me?.id ?? null)
  })
}

// Awaited before the first render rather than loaded in an effect: a catalogue that arrives after
// mount means the app paints once in English and then swaps, which is worse than one chunk fetch on
// a cold cache. Top-level await is fine here, main.tsx is an ES module.
// A failed chunk fetch must not leave a blank page: fall back to English, then to no catalogue.
try {
  await loadLocale(resolveInitialLocale())
} catch (err) {
  console.error('Failed to load the locale catalogue, falling back to English', err)
  try {
    await loadLocale('en')
  } catch (enErr) {
    console.error('Failed to load the English catalogue', enErr)
    i18n.loadAndActivate({ locale: 'en', messages: {} })
  }
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppI18nProvider>
      <AppThemeProvider>
        {/* Above every modal, not Mantine's default 400. The Discover detail modal sits at 1000 and
            the fullscreen "Show more" modal above that, so a toast raised by an action taken inside
            one of them rendered behind it: the action worked, said so, and the reader saw nothing. */}
        <Notifications autoClose={6000} zIndex={2000} />
        <QueryClientProvider client={queryClient}>
          <HistoryRouter history={appHistory}>
            <PageTransitionCommit />
            <App />
          </HistoryRouter>
        </QueryClientProvider>
      </AppThemeProvider>
    </AppI18nProvider>
  </StrictMode>,
)

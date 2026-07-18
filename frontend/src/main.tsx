import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { Notifications, notifications } from '@mantine/notifications'
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { BrowserRouter } from 'react-router-dom'
import '@mantine/core/styles.css'
import '@mantine/charts/styles.css'
import '@mantine/notifications/styles.css'
import './theme.css'
import { AppThemeProvider } from './theme-context'
import App from './App.tsx'

/**
 * One place that reports failures, so no call site can swallow one by forgetting a handler —
 * which is exactly how the series monitor toggle ended up reverting silently. Call sites only
 * need their own `onError` for extra work (resetting local state); the toast is automatic.
 *
 * `meta.errorMessage` overrides the text; `meta.silent` opts out entirely for flows that show
 * failure inline (bulk actions with per-row results).
 */
function reportError(error: unknown, meta?: Record<string, unknown>) {
  if (meta?.silent) return
  notifications.show({
    message: typeof meta?.errorMessage === 'string' ? meta.errorMessage : String(error),
    color: 'red',
  })
}

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      retry: 1,
      refetchOnWindowFocus: false,
    },
  },
  // Background refetches fail silently by design — a toast on every poll while the API is down
  // would bury the app. Only surface a query error when there's no data to fall back on.
  queryCache: new QueryCache({
    onError: (error, query) => {
      if (query.state.data === undefined) reportError(error, query.meta)
    },
  }),
  mutationCache: new MutationCache({
    onError: (error, _vars, _ctx, mutation) => reportError(error, mutation.meta),
  }),
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AppThemeProvider>
      <Notifications autoClose={6000} />
      <QueryClientProvider client={queryClient}>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </QueryClientProvider>
    </AppThemeProvider>
  </StrictMode>,
)

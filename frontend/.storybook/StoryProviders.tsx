import { useState, type ReactNode } from 'react'
import { MantineThemeProvider } from '@mantine/core'
import { Notifications, notifications } from '@mantine/notifications'
import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ApiError } from '../src/api/client'
import { AppThemeProvider } from '../src/theme-context'
import { AppI18nProvider } from '../src/i18n-context'
import { AuthProvider } from '../src/auth/AuthProvider'
import { NavHistoryProvider } from '../src/lib/navHistory'
import { ShellTitleProvider } from '../src/lib/shellTitle'
import { DocumentTitleProvider } from '../src/lib/documentTitle'

// Mantine's Transition waits two animation frames plus its duration before a modal, drawer or menu
// counts as entered, and under a loaded test run `toBeVisible` lands in between. With reduced motion
// respected it goes straight to entered. Only under the runner (vitest.config emulates
// `reducedMotion: 'reduce'`); the interactive Storybook animates like the app.
function TestMotion({ children, real }: { children: ReactNode; real?: boolean }) {
  if (real || !navigator.webdriver) return children
  return <MantineThemeProvider theme={{ respectReducedMotion: true }}>{children}</MantineThemeProvider>
}

function reportError(error: unknown, meta?: Record<string, unknown>) {
  if (meta?.silent) return
  if (meta?.inlineNotFound && error instanceof ApiError && error.status === 404) return
  const message =
    typeof meta?.errorMessage === 'string' ? meta.errorMessage : error instanceof Error ? error.message : String(error)
  notifications.show({ message, color: 'var(--danger)' })
}

export function StoryProviders({
  children,
  router,
  realMotion,
}: {
  children: ReactNode
  router?: { path?: string; initialEntries?: string[] }
  realMotion?: boolean
}) {
  const [client] = useState(
    () =>
      new QueryClient({
        defaultOptions: { queries: { retry: false, refetchOnWindowFocus: false } },
        // The same failure toasts main.tsx raises, minus its dedupe, so error stories look like the app.
        queryCache: new QueryCache({
          onError: (error, query) => {
            if (query.state.data === undefined) reportError(error, query.meta)
          },
        }),
        mutationCache: new MutationCache({ onError: (error, _v, _c, mutation) => reportError(error, mutation.meta) }),
      }),
  )
  const content = router?.path ? (
    <Routes>
      <Route path={router.path} element={children} />
    </Routes>
  ) : (
    children
  )
  // Same order as main.tsx and App.tsx, so hooks find every context they would in the app.
  return (
    <AppI18nProvider>
      <AppThemeProvider>
        <TestMotion real={realMotion}>
          <Notifications autoClose={6000} zIndex={2000} />
          <QueryClientProvider client={client}>
            <MemoryRouter initialEntries={router?.initialEntries ?? (router?.path ? [router.path] : ['/'])}>
              <AuthProvider>
                <NavHistoryProvider>
                  <ShellTitleProvider>
                    <DocumentTitleProvider>{content}</DocumentTitleProvider>
                  </ShellTitleProvider>
                </NavHistoryProvider>
              </AuthProvider>
            </MemoryRouter>
          </QueryClientProvider>
        </TestMotion>
      </AppThemeProvider>
    </AppI18nProvider>
  )
}

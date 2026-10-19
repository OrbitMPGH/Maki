import type { Decorator, Preview } from '@storybook/react-vite'
import { mswLoader } from 'msw-storybook-addon/csf3'
import { notifications } from '@mantine/notifications'
import '@mantine/core/styles.css'
import '@mantine/notifications/styles.css'
import '@fontsource-variable/onest'
import '@fontsource-variable/bricolage-grotesque/opsz.css'
import '../src/theme.css'
import { ACCENTS, BACKGROUNDS } from '../src/theme-context'
import { loadLocale, SUPPORTED_LOCALES, type LocaleCode } from '../src/i18n'
import { StoryProviders } from './StoryProviders'
import { NOW, readerMe, signedInAs } from './mocks/api'
import { defaultHandlers, unmockedRequests } from './mocks'

/**
 * Story parameters this preview understands, on top of Storybook's own:
 *
 * - `router: { path?, initialEntries? }`: where the story sits. `path` is a route pattern
 *   (`/series/:id`) so `useParams` works; `initialEntries` is the URL it starts on.
 * - `auth: 'admin' | 'reader' | 'signedOut'`: who `/auth/me` says is signed in. Default admin.
 * - `msw: [...handlers]`: per-story overrides; they win over the defaults in `mocks/`.
 * - `allowUnmocked: true`: don't fail the story when a request reached no handler.
 * - `realMotion: true`: keep Mantine's transitions under the test runner (see StoryProviders).
 */
declare module 'storybook/internal/csf' {
  interface Parameters {
    router?: { path?: string; initialEntries?: string[] }
    auth?: 'admin' | 'reader' | 'signedOut'
    allowUnmocked?: boolean
    realMotion?: boolean
  }
}

// Every story runs on the same date, so relative times, streaks and "new this week" stay put. The
// clock still ticks (timers, durations and animations need it to); it just starts at NOW.
function shiftClock(to: Date) {
  const Real = Date
  const offset = to.getTime() - Real.now()
  class ShiftedDate extends Real {
    constructor(...args: unknown[]) {
      if (args.length === 0) super(Real.now() + offset)
      else super(...(args as [number]))
    }
    static now() {
      return Real.now() + offset
    }
  }
  globalThis.Date = ShiftedDate as DateConstructor
}
shiftClock(NOW)

// Under the test runner, entrance animations (`.auth-panel` fades in from opacity 0) make
// `toBeVisible` race the keyframes. A browser person keeps the motion; Playwright gets none.
if (navigator.webdriver) {
  const style = document.createElement('style')
  style.textContent =
    '*,*::before,*::after{animation-duration:0s!important;animation-delay:0s!important;transition-duration:0s!important;transition-delay:0s!important}'
  document.head.appendChild(style)
}

// Pages keep filters, search text and view modes in sessionStorage (`usePageState`) and localStorage,
// and Mantine's toast store is module-wide, so without this a story starts where the previous one
// left off. Storybook's own UI state shares the origin and is kept.
function resetBrowserState() {
  for (const storage of [localStorage, sessionStorage]) {
    try {
      for (const key of Object.keys(storage)) if (!/storybook/i.test(key)) storage.removeItem(key)
    } catch {
      // Storage blocked: nothing to leak.
    }
  }
  notifications.clean()
  notifications.cleanQueue()
}

// AppThemeProvider reads the choice from localStorage once on mount, so the toolbar writes it there
// and remounts the tree through `key`.
const withProviders: Decorator = (Story, { globals, parameters }) => {
  try {
    localStorage.setItem('maki-background', globals.background)
    localStorage.setItem('maki-accent', globals.accent)
  } catch {
    // Storage blocked: the defaults still render.
  }
  return (
    <StoryProviders key={`${globals.background}-${globals.accent}-${globals.locale}`} router={parameters.router}
      realMotion={parameters.realMotion}
    >
      <Story />
    </StoryProviders>
  )
}

const preview: Preview = {
  decorators: [withProviders],
  loaders: [
    mswLoader(async () => {
      const { setupWorker } = await import('msw/browser')
      // Initial handlers rather than `msw.use` in beforeEach: the loader resets to these before every
      // story and then applies `parameters.msw`, which therefore wins.
      const worker = setupWorker(...defaultHandlers)
      await worker.start({ quiet: true, onUnhandledRequest: 'bypass' })
      return worker
    }),
    // main.tsx awaits the catalogue before the first render; without it every string renders as its id.
    async ({ globals }) => {
      await loadLocale(globals.locale as LocaleCode)
      return {}
    },
  ],
  async beforeEach({ msw, parameters }) {
    unmockedRequests.length = 0
    resetBrowserState()
    // Admin comes from the default `/auth/me` handler, so a `signedInAs` in `parameters.msw` still wins.
    if (parameters.auth === 'reader') msw.use(signedInAs(readerMe))
    else if (parameters.auth === 'signedOut') msw.use(signedInAs(null))
  },
  async afterEach({ parameters }) {
    if (parameters.allowUnmocked || unmockedRequests.length === 0) return
    const missing = [...new Set(unmockedRequests)]
    unmockedRequests.length = 0
    throw new Error(`Requests with no MSW handler (add them under .storybook/mocks):\n  ${missing.join('\n  ')}`)
  },
  initialGlobals: {
    background: 'tinted',
    accent: 'indigo',
    locale: 'en',
  },
  globalTypes: {
    background: {
      description: 'Background',
      toolbar: {
        icon: 'contrast',
        items: BACKGROUNDS.filter((b) => b.id !== 'system').map((b) => ({ value: b.id, title: b.label.message ?? b.id })),
        dynamicTitle: true,
      },
    },
    accent: {
      description: 'Accent',
      toolbar: {
        icon: 'paintbrush',
        items: ACCENTS.map((a) => ({ value: a.id, title: a.label.message ?? a.id })),
        dynamicTitle: true,
      },
    },
    locale: {
      description: 'Language',
      toolbar: {
        icon: 'globe',
        items: SUPPORTED_LOCALES.map((l) => ({ value: l.code, title: l.label })),
        dynamicTitle: true,
      },
    },
  },
  parameters: {
    controls: {
      matchers: {
        color: /(background|color)$/i,
        date: /Date$/i,
      },
    },
    a11y: {
      // 'todo' - show a11y violations in the test UI only
      // 'error' - fail CI on a11y violations
      // 'off' - skip a11y checks entirely
      test: 'todo',
    },
  },
}

export default preview

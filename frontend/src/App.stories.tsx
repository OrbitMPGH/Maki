import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../.storybook/mocks/api'
import { setupStatus, uiSettings } from '../.storybook/mocks/shared'
import App from './App'
import * as ActivityStories from './pages/ActivityPage.stories'
import * as AddSeriesStories from './pages/AddSeriesPage.stories'
import * as CreatorStories from './pages/CreatorPage.stories'
import * as DiscoverStories from './pages/DiscoverPage.stories'
import * as HealthStories from './pages/HealthPage.stories'
import * as HomeStories from './pages/HomePage.stories'
import * as ImportStories from './pages/ImportPage.stories'
import * as LibraryStories from './pages/LibraryPage.stories'
import * as NotificationsStories from './pages/NotificationsPage.stories'
import * as RequestsStories from './pages/RequestsPage.stories'
import * as ScrobbleStories from './pages/ScrobblePage.stories'
import * as SeriesStories from './pages/SeriesDetailPage.stories'
import * as SettingsStories from './pages/SettingsPage.stories'
import * as StatsStories from './pages/StatsPage.stories'

/**
 * The whole app at each route: shell, navigation, header actions and the page. The page stories are
 * where states are explored; these show how each page sits in the chrome, and each one takes its
 * mocks from the page story it mirrors, so changing that story's data changes this one too.
 */
const meta = {
  component: App,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
} satisfies Meta<typeof App>

export default meta
type Story = StoryObj<typeof meta>

type Handlers = NonNullable<Story['parameters']>['msw']
type Hook = (context: Parameters<NonNullable<Extract<Story['beforeEach'], (...args: never[]) => unknown>>>[0]) => unknown
type StoryLike = { parameters?: Story['parameters']; beforeEach?: unknown }

/**
 * The page story's mocks: its own `msw` if it sets one, else its file's. Storybook replaces a
 * file-level `msw` with a story-level one rather than merging, so that is the set the page story
 * actually renders with. Pages whose stories set none run on the global mocks, as these do anyway.
 */
const mocksOf = (page: { default: StoryLike }, story: StoryLike): Handlers[] =>
  (story.parameters?.msw ?? page.default.parameters?.msw ?? []) as Handlers[]

/** The file's then the story's `beforeEach` (seeded prefs, layered inbox handlers), as Storybook runs them. */
const hooksOf = (page: { default: StoryLike }, story: StoryLike): Hook[] =>
  [page.default.beforeEach, story.beforeEach].flat().filter((hook): hook is Hook => typeof hook === 'function')

/**
 * The app at `path`, set up exactly as the page story it mirrors (mocks and `beforeEach`), so a change
 * to that story shows here too. Extra handlers go first and win.
 */
const mirror = (path: string, page: { default: StoryLike }, story: StoryLike, extra: Handlers[] = []): Story => ({
  parameters: { router: { initialEntries: [path] }, msw: [...extra, ...mocksOf(page, story)] },
  beforeEach: async (context) => {
    const cleanups: (() => unknown)[] = []
    for (const hook of hooksOf(page, story)) {
      const cleanup = await hook(context)
      if (typeof cleanup === 'function') cleanups.push(cleanup as () => unknown)
    }
    return async () => {
      for (const cleanup of cleanups.reverse()) await cleanup()
    }
  },
})

const home = (extra?: Handlers[]) => mirror('/home', HomeStories, HomeStories.Default, extra)
const library = (extra?: Handlers[]) => mirror('/library', LibraryStories, LibraryStories.Grid, extra)

export const Home: Story = {
  ...home(),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: /library/i })).toBeVisible()
  },
}
// `/` is a redirect, not a page; with the start page set to Library it lands there.
export const StartPageRedirect: Story = mirror('/', LibraryStories, LibraryStories.Grid, [
  http.get(api('/settings/ui'), () => HttpResponse.json({ ...uiSettings, startPage: 'library' })),
])
export const Library: Story = library()
export const Series: Story = mirror('/series/1', SeriesStories, SeriesStories.Ongoing)
export const AddSeries: Story = mirror('/add', AddSeriesStories, AddSeriesStories.BrowsingTheCatalogue)
export const Creator: Story = mirror('/creator/Aoi%20Mizuhara', CreatorStories, CreatorStories.Default)
export const Discover: Story = mirror('/discover', DiscoverStories, DiscoverStories.Browse)
export const DiscoverRecommended: Story = mirror('/discover/recommended', DiscoverStories, DiscoverStories.Recommended)
export const DiscoverTaste: Story = mirror('/discover/taste', DiscoverStories, DiscoverStories.YourTaste)
export const Import: Story = mirror('/import', ImportStories, ImportStories.BeforeScanning)
export const Activity: Story = mirror('/activity', ActivityStories, ActivityStories.Default)
export const Requests: Story = mirror('/requests', RequestsStories, RequestsStories.PendingQueue)
export const Scrobble: Story = mirror('/scrobble', ScrobbleStories, ScrobbleStories.Default)
export const Stats: Story = mirror('/stats', StatsStories, StatsStories.Default)
export const Notifications: Story = mirror('/notifications', NotificationsStories, NotificationsStories.Default)
export const Settings: Story = mirror('/settings', SettingsStories, SettingsStories.AdminAllTabs)
export const Health: Story = mirror('/health', HealthStories, HealthStories.Healthy)
export const NotFound: Story = {
  parameters: { router: { initialEntries: ['/no/such/page'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 1, name: /page not found/i })).toBeVisible()
  },
}

export const AsReader: Story = {
  ...library(),
  parameters: { ...library().parameters, auth: 'reader' },
  play: async ({ canvas }) => {
    await canvas.findByRole('link', { name: /library/i })
    // Admin-only header action.
    await expect(canvas.queryByRole('button', { name: /health issues/i })).not.toBeInTheDocument()
  },
}

export const SignedOut: Story = {
  parameters: { router: { initialEntries: ['/library'] }, auth: 'signedOut' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: /sign in/i })).toBeVisible()
  },
}

export const FirstRun: Story = home([
  http.get(api('/settings/setup'), () => HttpResponse.json({ ...setupStatus, completed: false })),
])

export const MobileNavOpen: Story = {
  ...home(),
  parameters: { ...home().parameters, viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const burger = await canvas.findByRole('button', { name: /menu/i })
    await userEvent.click(burger)
    await waitFor(() => expect(burger).toHaveAttribute('aria-expanded', 'true'))
    await expect((await within(canvasElement).findAllByRole('link', { name: /settings/i }))[0]).toBeVisible()
  },
}

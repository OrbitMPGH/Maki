import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../.storybook/mocks/api'
import { setupStatus, uiSettings } from '../.storybook/mocks/shared'
import { scrobbleHandlers } from '../.storybook/mocks/insights'
import App from './App'

/**
 * The whole app at each route: shell, navigation, header actions and the page, all against the same
 * mocks the per-page stories use. The page stories are where states are explored; these show how
 * each page sits in the chrome.
 */
const meta = {
  component: App,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
} satisfies Meta<typeof App>

export default meta
type Story = StoryObj<typeof meta>

const at = (path: string): Story => ({ parameters: { router: { initialEntries: [path] } } })

export const Home: Story = {
  ...at('/home'),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: /library/i })).toBeVisible()
  },
}
export const StartPageRedirect: Story = {
  ...at('/'),
  parameters: {
    router: { initialEntries: ['/'] },
    msw: [http.get(api('/settings/ui'), () => HttpResponse.json({ ...uiSettings, startPage: 'library' }))],
  },
}
export const Library: Story = at('/library')
export const Series: Story = at('/series/1')
export const AddSeries: Story = at('/add')
export const Creator: Story = at('/creator/Aoi%20Mizuhara')
export const Discover: Story = at('/discover')
export const DiscoverRecommended: Story = at('/discover/recommended')
export const DiscoverTaste: Story = at('/discover/taste')
export const Import: Story = at('/import')
export const Activity: Story = at('/activity')
export const Requests: Story = at('/requests')
// Settings registers its own /scrobble/status first, so without this the page gets the Settings fixture.
export const Scrobble: Story = { parameters: { router: { initialEntries: ['/scrobble'] }, msw: scrobbleHandlers() } }
export const Stats: Story = at('/stats')
export const Notifications: Story = at('/notifications')
export const Settings: Story = at('/settings')
export const Health: Story = at('/health')
export const NotFound: Story = {
  ...at('/no/such/page'),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 1, name: /page not found/i })).toBeVisible()
  },
}

export const AsReader: Story = {
  parameters: { router: { initialEntries: ['/library'] }, auth: 'reader' },
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

export const FirstRun: Story = {
  parameters: {
    router: { initialEntries: ['/home'] },
    msw: [http.get(api('/settings/setup'), () => HttpResponse.json({ ...setupStatus, completed: false }))],
  },
}

export const MobileNavOpen: Story = {
  parameters: { router: { initialEntries: ['/home'] }, viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const burger = await canvas.findByRole('button', { name: /menu/i })
    await userEvent.click(burger)
    await waitFor(() => expect(burger).toHaveAttribute('aria-expanded', 'true'))
    await expect((await within(canvasElement).findAllByRole('link', { name: /settings/i }))[0]).toBeVisible()
  },
}

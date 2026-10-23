import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import {
  downloadingItems,
  failedItem,
  homePage,
  homeUi,
  progressSummary,
  queuePage,
} from '../../.storybook/mocks/home'
import { metadataSettings } from '../../.storybook/mocks/shared'
import HomePage from './HomePage'

const meta = {
  component: HomePage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', router: { initialEntries: ['/'] }, msw: homePage() },
} satisfies Meta<typeof HomePage>

export default meta
type Story = StoryObj<typeof meta>

const layout = (options: Parameters<typeof homeUi>[0]) => http.get(api('/settings/ui'), () => HttpResponse.json(homeUi(options)))

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Continue reading')).toBeVisible()
    await expect(await canvas.findByRole('heading', { level: 2, name: /recently added/i })).toBeVisible()
    await expect(await canvas.findByRole('heading', { level: 2, name: /you might like/i })).toBeVisible()
    await expect(await canvas.findByRole('heading', { level: 2, name: /currently popular/i })).toBeVisible()
  },
}

export const ReadingSectionsAsRails: Story = {
  parameters: { msw: homePage(layout({ hero: { continue: false, jumpback: false } })) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: /jump back in/i })).toBeVisible()
    await expect(canvas.queryByRole('heading', { name: /next up|next to jump back in/i })).toBeNull()
  },
}

export const JumpBackInHero: Story = {
  parameters: { msw: homePage(layout({ hero: { continue: true, jumpback: true } })) },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('heading', { level: 2, name: 'Next up' })).toBeVisible())
    await waitFor(() => expect(canvas.getByRole('heading', { level: 2, name: 'Next to jump back in' })).toBeVisible())
  },
}

export const DownloadingBacklog: Story = {
  parameters: {
    msw: homePage(http.get(api('/queue'), () => HttpResponse.json(queuePage([...downloadingItems, failedItem])))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: /downloading now/i })).toBeVisible()
    await expect(await canvas.findByText(/3 more in the queue/i)).toBeVisible()
  },
}

export const NothingDownloading: Story = {
  parameters: { msw: homePage(http.get(api('/queue'), () => HttpResponse.json(queuePage([failedItem])))) },
  play: async ({ canvas }) => {
    await canvas.findByText('Continue reading')
    await expect(canvas.queryByRole('heading', { level: 2, name: /downloading now/i })).toBeNull()
  },
}

export const CustomRails: Story = {
  parameters: { msw: homePage(layout({ rails: [1, 2, 3] })) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Unread favourites' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Cozy picks' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Top rated romance' })).toBeVisible()
  },
}

export const GlanceRidesTheHero: Story = {
  play: async ({ canvas, canvasElement }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await waitFor(() => expect(glance).toHaveAttribute('data-hosted'))
    await expect(canvasElement.querySelector('.continue-section')).toContainElement(glance)
  },
}

/** The "Next up beside the hero" switch: the list in a card beside the hero, no tray; the glance sits in or under the hero by width. */
export const NextUpBesideTheHero: Story = {
  parameters: { msw: homePage(layout({ options: { continue: { nextUpBeside: true } } })) },
  play: async ({ canvas, canvasElement }) => {
    const list = await canvas.findByRole('region', { name: 'Next up' })
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(canvasElement.querySelector('.continue-lead')).toContainElement(list)
    await expect(canvasElement.querySelector('.continue-section')).toContainElement(glance)
    await expect(canvasElement.querySelector('.continue-tray')).toBeNull()
  },
}

/** The glance belongs to Continue reading, so switching that section off takes it along. */
export const GlanceGoneWithContinueOff: Story = {
  parameters: { msw: homePage(layout({ off: ['continue'], options: { continue: { glance: true } } })) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: /recently added/i })).toBeVisible()
    await expect(canvas.queryByRole('region', { name: 'Last 5 weeks' })).toBeNull()
  },
}

/** Nothing read in the last five weeks: no card of zeros above the hero. */
export const GlanceHiddenUntilFirstDayRead: Story = {
  parameters: { msw: homePage(http.get(api('/progress/heatmap'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Lanterns Over Kasumi' })).toBeVisible()
    await expect(canvas.queryByRole('region', { name: 'Last 5 weeks' })).toBeNull()
  },
}

/** The glance is a switch on Continue reading; off, it is gone and nothing fetches for it. */
export const GlanceSwitchedOff: Story = {
  parameters: { msw: homePage(layout({ options: { continue: { glance: false } } })) },
  play: async ({ canvas }) => {
    await canvas.findByRole('heading', { level: 2, name: 'Lanterns Over Kasumi' })
    await expect(canvas.queryByRole('region', { name: 'Last 5 weeks' })).toBeNull()
  },
}

/** Continue reading as a plain rail: the glance is a strip above it, at the section's slot. */
export const GlanceWithoutAHero: Story = {
  parameters: { msw: homePage(layout({ hero: { continue: false } })) },
  play: async ({ canvas }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(glance).not.toHaveAttribute('data-hosted')
    await expect(await canvas.findByRole('heading', { level: 2, name: /continue reading/i })).toBeVisible()
  },
}

export const GlanceWithStreaksOff: Story = {
  parameters: {
    msw: homePage(http.get(api('/progress/summary'), () => HttpResponse.json({ ...progressSummary, showStreaks: false }))),
  },
  play: async ({ canvas }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(within(glance).getByText('Days read')).toBeVisible()
    await expect(within(glance).queryByLabelText(/day streak/)).toBeNull()
  },
}

export const ProgressionSwitchedOff: Story = {
  parameters: {
    msw: homePage(http.get(api('/progress/summary'), () => HttpResponse.json({ ...progressSummary, enabled: false }))),
  },
  play: async ({ canvas }) => {
    await canvas.findByRole('heading', { level: 2, name: 'Lanterns Over Kasumi' })
    await expect(canvas.queryByRole('region', { name: 'Last 5 weeks' })).toBeNull()
  },
}

export const NoReadTracking: Story = {
  parameters: {
    msw: homePage(
      http.get(api('/reader/used'), () => HttpResponse.json({ used: false })),
      http.get(api('/home/reading'), () => HttpResponse.json({ continueReading: [], jumpBackIn: [] })),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/connect kavita/i)).toBeVisible()
    await expect(canvas.queryByRole('region', { name: 'Last 5 weeks' })).toBeNull()
  },
}

export const NothingToPickUp: Story = {
  parameters: {
    msw: homePage(http.get(api('/home/reading'), () => HttpResponse.json({ continueReading: [], jumpBackIn: [] }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing to pick up yet')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('link', { name: /browse library/i })).toHaveAttribute('href', '/library'))
  },
}

export const ReadingFailed: Story = {
  parameters: { msw: homePage(http.get(api('/home/reading'), () => failWith(500))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/couldn't load your reading progress/i)).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

export const SectionsLoading: Story = {
  parameters: {
    msw: homePage(
      http.get(api('/home/reading'), () => neverResolve()),
      http.get(api('/home/recently-added'), () => neverResolve()),
      http.get(api('/home/from-anime'), () => neverResolve()),
    ),
  },
}

export const SettingsLoading: Story = {
  parameters: { msw: homePage(http.get(api('/settings/ui'), () => neverResolve())) },
}

export const EmptyLibrary: Story = {
  parameters: { msw: homePage(http.get(api('/series'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing in your library yet')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('link', { name: 'Add series' })).toHaveAttribute('href', '/add'))
    await waitFor(() => expect(canvas.getByRole('link', { name: /import a folder/i })).toHaveAttribute('href', '/import'))
  },
}

export const EmptyLibraryAsReader: Story = {
  parameters: { auth: 'reader', msw: homePage(http.get(api('/series'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: 'Request series' })).toBeVisible()
    await expect(canvas.queryByRole('link', { name: /import a folder/i })).toBeNull()
  },
}

export const LibraryFailed: Story = {
  parameters: { msw: homePage(http.get(api('/series'), () => failWith(500))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/couldn't load your library/i)).toBeVisible()
  },
}

export const EverySectionOff: Story = {
  parameters: {
    msw: homePage(
      layout({ off: ['downloading', 'continue', 'jumpback', 'fromanime', 'recent', 'following', 'recommended', 'popular'] }),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Every section is switched off')).toBeVisible()
  },
}

export const WithoutLocalDatabase: Story = {
  parameters: {
    msw: homePage(
      layout({ rails: [1, 2, 3] }),
      http.get(api('/settings/metadata'), () => HttpResponse.json({ ...metadataSettings, useLocalDb: false, dumpPresent: false })),
    ),
  },
  play: async ({ canvas }) => {
    // Only the library rail survives: the other sources need the MangaBaka dump.
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Unread favourites' })).toBeVisible()
    await expect(canvas.queryByRole('heading', { level: 2, name: /currently popular/i })).toBeNull()
    await expect(canvas.queryByRole('heading', { level: 2, name: 'Cozy picks' })).toBeNull()
  },
}

export const AsReader: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Continue reading')).toBeVisible()
  },
}

export const EditLayout: Story = {
  parameters: { router: { initialEntries: ['/?edit=1'] }, msw: homePage(layout({ rails: [1, 2] })) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Editing layout')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Done' })).toBeVisible()
  },
}

export const EditLayoutLoadFailed: Story = {
  parameters: {
    router: { initialEntries: ['/?edit=1'] },
    msw: homePage(http.get(api('/rails'), () => failWith(500))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The layout could not be loaded.')).toBeVisible()
  },
}

export const OpenLayoutEditor: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Edit layout' }))
    await expect(await canvas.findByText('Editing layout')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Cancel' }))
    await expect(await canvas.findByText('Continue reading')).toBeVisible()
  },
}

export const AddRailFromHome: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Add a rail' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByRole('dialog', { name: 'New rail' })).toBeVisible())
  },
}

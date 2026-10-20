import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { metadataSettings } from '../../.storybook/mocks/shared'
import {
  discoverPage,
  recommendationsPage,
  resetTabState,
  savedRecommendationDefaults,
  tasteInsightsUnavailable,
  tasteProfileEmpty,
  readingBehaviourEmpty,
} from '../../.storybook/mocks/discover'
import DiscoverPage from './DiscoverPage'

const route = (initial: string) => ({ path: '/discover/:tab?', initialEntries: [initial] })

const meta = {
  component: DiscoverPage,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  parameters: { layout: 'fullscreen', router: route('/discover'), msw: discoverPage() },
} satisfies Meta<typeof DiscoverPage>

export default meta
type Story = StoryObj<typeof meta>

const nothingPersonal = [
  http.get(api('/recommendations/discover/recent'), () => HttpResponse.json(null)),
  http.get(api('/recommendations/discover/side-interests'), () => HttpResponse.json([])),
  http.post(api('/recommendations/discover/cohort'), () => HttpResponse.json(null)),
  http.get(api('/recommendations/discover/following'), () => HttpResponse.json({ creators: [] })),
  http.get(api('/recommendations/taste-profile'), () => HttpResponse.json(tasteProfileEmpty)),
]

export const Browse: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Trending this week' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Readers like you also finished' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'New from creators you follow' })).toBeVisible()
  },
}

export const BrowseLoading: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/recommendations/discover'), () => neverResolve()),
      http.get(api('/recommendations/discover/recent'), () => neverResolve()),
      http.get(api('/recommendations/discover/side-interests'), () => neverResolve()),
      http.get(api('/recommendations/discover/genres'), () => neverResolve()),
      http.post(api('/recommendations/discover/cohort'), () => neverResolve()),
      http.get(api('/recommendations/discover/following'), () => neverResolve()),
    ),
  },
}

export const BrowseError: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/discover'), () => failWith(500, 'The catalogue query timed out'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Catalogue unavailable')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Try again' })).toBeVisible()
  },
}

export const NoLocalDatabase: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/settings/metadata'), () => HttpResponse.json({ ...metadataSettings, dumpPresent: false })),
      http.get(api('/recommendations/discover'), () =>
        failWith(400, 'The local MangaBaka database is not installed.', 'error.metadata.noDump'),
      ),
      http.get(api('/recommendations/discover/genres'), () => HttpResponse.json([])),
      ...nothingPersonal,
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Catalogue unavailable')).toBeVisible()
  },
}

export const BrowseEmptyCatalogue: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/recommendations/discover'), () => HttpResponse.json([])),
      http.get(api('/recommendations/discover/genres'), () => HttpResponse.json([])),
      ...nothingPersonal,
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing to browse yet')).toBeVisible()
  },
}

export const BrowseNewReader: Story = {
  parameters: { msw: discoverPage(...nothingPersonal) },
}

export const BrowseReader: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Trending this week' })).toBeVisible()
  },
}

export const DetailModal: Story = {
  play: async ({ canvas, canvasElement }) => {
    const cards = await canvas.findAllByRole('button', { name: /^View and add / })
    cards[0].click()
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByText('Synopsis')).toBeVisible()
  },
}

export const ExpandedRail: Story = {
  play: async ({ canvas, canvasElement }) => {
    const buttons = await canvas.findAllByRole('button', { name: 'Show more' })
    buttons[0].click()
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByText('Hidden everywhere (2)')).toBeVisible()
  },
}

export const EditLayout: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Edit layout' }))
    await waitFor(() => expect(canvas.getByRole('tab', { name: 'Recommended' })).toBeDisabled())
  },
}

export const Recommended: Story = {
  parameters: { router: route('/discover/recommended') },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Because of what you collect' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Related to your library' })).toBeVisible()
  },
}

export const RecommendedCustomizeOpen: Story = {
  parameters: { router: route('/discover/recommended') },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Customize' }))
    await expect(await canvas.findByText('Seed from')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Apply' })).toBeVisible()
  },
}

export const RecommendedWithSavedDefault: Story = {
  parameters: {
    router: route('/discover/recommended'),
    msw: discoverPage(http.get(api('/recommendations/defaults'), () => HttpResponse.json(savedRecommendationDefaults))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Customized' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Feels like your seeds' })).toBeVisible()
  },
}

export const RecommendedShowMore: Story = {
  parameters: { router: route('/discover/recommended') },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Show more' }))
    await waitFor(() => expect(canvas.getAllByRole('button', { name: /^View and add / })).not.toHaveLength(0))
  },
}

export const RecommendedLoading: Story = {
  parameters: {
    router: route('/discover/recommended'),
    msw: discoverPage(http.post(api('/recommendations'), () => neverResolve())),
  },
}

export const RecommendedEmpty: Story = {
  parameters: {
    router: route('/discover/recommended'),
    msw: discoverPage(
      http.post(api('/recommendations'), () =>
        HttpResponse.json({ ...recommendationsPage(0), related: [], similar: [], hasMore: false }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing to recommend yet')).toBeVisible()
  },
}

export const RecommendedError: Story = {
  parameters: {
    router: route('/discover/recommended'),
    msw: discoverPage(http.post(api('/recommendations'), () => failWith(500, 'The recommender is not available'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The recommender is not available')).toBeVisible()
  },
}

export const YourTaste: Story = {
  parameters: { router: route('/discover/taste') },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('How you read', {}, { timeout: 8000 })).toBeVisible()
  },
}

export const YourTasteEmpty: Story = {
  parameters: {
    router: route('/discover/taste'),
    msw: discoverPage(
      http.get(api('/recommendations/taste-profile'), () => HttpResponse.json(tasteProfileEmpty)),
      http.get(api('/recommendations/taste-insights'), () => HttpResponse.json(tasteInsightsUnavailable)),
      http.get(api('/recommendations/reading-behaviour'), () => HttpResponse.json(readingBehaviourEmpty)),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing to profile yet', {}, { timeout: 8000 })).toBeVisible()
  },
}

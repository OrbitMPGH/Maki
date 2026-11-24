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
import { shortlistEntries, shortlistMock } from '../../.storybook/mocks/shortlist'
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
  play: async ({ canvas, canvasElement, userEvent }) => {
    const buttons = await canvas.findAllByRole('button', { name: 'Show more' })
    buttons[0].click()
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog')
    await expect(await within(dialog).findByRole('toolbar', { name: 'Filters' })).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'More filter actions' }))
    await expect(await body.findByRole('menuitem', { name: 'Hidden everywhere (2)' })).toBeVisible()
  },
}

export const EditLayout: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More options' }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('menuitem', { name: 'Edit layout' }))
    await waitFor(() => expect(canvas.getByRole('tab', { name: 'Recommended' })).toBeDisabled())
  },
}

export const Recommended: Story = {
  parameters: { router: route('/discover/recommended') },
  play: async ({ canvas }) => {
    const similarTab = await canvas.findByRole('tab', { name: /Because of what you collect/ })
    await expect(similarTab).toHaveAttribute('aria-selected', 'true')
    await expect(await canvas.findByRole('tab', { name: /Related to your library/ })).toBeVisible()
    // Page 2 auto-loads as the sentinel enters the viewport, with no Show more button.
    await expect(await canvas.findByText('All 9 picks shown')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Show more' })).toBeNull()
  },
}

export const RecommendedRelatedTab: Story = {
  parameters: { router: route('/discover/recommended') },
  play: async ({ canvas, userEvent }) => {
    const similarTab = await canvas.findByRole('tab', { name: /Because of what you collect/ })
    similarTab.focus()
    await userEvent.keyboard('{ArrowRight}')
    const relatedTab = await canvas.findByRole('tab', { name: /Related to your library/ })
    await expect(relatedTab).toHaveAttribute('aria-selected', 'true')
    await expect(await canvas.findByRole('tabpanel', { name: /Related to your library/ })).toBeVisible()
    await expect(canvas.queryByRole('toolbar', { name: 'Filters' })).toBeNull()
    const sequelsChip = await canvas.findByRole('button', { name: /Sequels/ })
    await userEvent.click(sequelsChip)
    await waitFor(() => expect(canvas.getAllByRole('button', { name: /^View and add / })).toHaveLength(1))
  },
}

export const RecommendedRelationsOnly: Story = {
  parameters: {
    router: route('/discover/recommended'),
    msw: discoverPage(
      http.post(api('/recommendations'), () =>
        HttpResponse.json({
          ...recommendationsPage(0),
          similar: [],
          hasMore: false,
          totalSimilar: 0,
        }),
      ),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    const relatedTab = await canvas.findByRole('tab', { name: /Related to your library/ })
    await expect(relatedTab).toHaveAttribute('aria-selected', 'true')
    const similarTab = await canvas.findByRole('tab', { name: /Because of what you collect/ })
    await userEvent.click(similarTab)
    await expect(similarTab).toHaveAttribute('aria-selected', 'true')
    await expect(await canvas.findByRole('toolbar', { name: 'Filters' })).toBeVisible()
    await expect(await canvas.findByText('No matches')).toBeVisible()
  },
}

let nextPageFailedOnce = false

export const RecommendedNextPageRetry: Story = {
  beforeEach: () => {
    nextPageFailedOnce = false
  },
  parameters: {
    router: route('/discover/recommended'),
    msw: discoverPage(
      http.post(api('/recommendations'), async ({ request }) => {
        const body = (await request.json()) as { page?: number }
        if ((body.page ?? 0) === 1 && !nextPageFailedOnce) {
          nextPageFailedOnce = true
          return failWith(500, 'Pool slice timed out')
        }
        return HttpResponse.json(recommendationsPage(body.page ?? 0))
      }),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('Could not load more recommendations.')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Try again' }))
    await expect(await canvas.findByText('All 9 picks shown')).toBeVisible()
  },
}

export const RecommendedNoRelated: Story = {
  parameters: {
    router: route('/discover/recommended'),
    msw: discoverPage(
      http.post(api('/recommendations'), async ({ request }) => {
        const body = (await request.json()) as { page?: number }
        return HttpResponse.json({ ...recommendationsPage(body.page ?? 0), related: [] })
      }),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('All 9 picks shown')).toBeVisible()
    await expect(canvas.queryByRole('tablist', { name: 'Recommendation views' })).toBeNull()
  },
}

export const RecommendedSeedsOpen: Story = {
  parameters: { router: route('/discover/recommended') },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Seeds' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('textbox', { name: 'Search series' })).toBeVisible()
    await expect(await body.findByText('With no series picked, your whole library is used.')).toBeVisible()
  },
}

export const RecommendedWithSavedDefault: Story = {
  parameters: {
    router: route('/discover/recommended'),
    msw: discoverPage(http.get(api('/recommendations/defaults'), () => HttpResponse.json(savedRecommendationDefaults))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Type: Manga' })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Rating: 7.0+' })).toBeVisible()
    await expect(await canvas.findByRole('tab', { name: /Feels like your seeds/ })).toHaveAttribute(
      'aria-selected',
      'true',
    )
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

const shortlistTab = shortlistMock(shortlistEntries)

export const Shortlist: Story = {
  beforeEach: () => shortlistTab.reset(),
  parameters: { router: route('/discover/shortlist'), msw: discoverPage(...shortlistTab.handlers) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Changed since you saved them' })).toBeVisible()
    await expect(await canvas.findByRole('tab', { name: /^Shortlist/ })).toHaveAttribute('aria-selected', 'true')
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

export const RecommendedDensityMenu: Story = {
  parameters: { router: route('/discover/recommended') },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More options' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('menuitem', { name: 'Compact' })).toBeVisible()
    await expect(body.queryByRole('menuitem', { name: 'Edit layout' })).toBeNull()
  },
}

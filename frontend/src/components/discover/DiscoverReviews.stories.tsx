import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../../.storybook/mocks/api'
import { discoverPage, reviews } from '../../../.storybook/mocks/discover'
import { DiscoverReviews, ReviewList } from './DiscoverReviews'

const meta = {
  component: DiscoverReviews,
  tags: ['ai-generated'],
  args: { malId: 5001 },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 360 }}>
        <Story />
      </div>
    ),
  ],
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverReviews>

export default meta
type Story = StoryObj<typeof meta>

const serve = (body: object | null) =>
  discoverPage(http.get(api('/recommendations/reviews/:malId'), () => HttpResponse.json(body)))

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('quietpageturner')).toBeVisible()
    await expect(await canvas.findByText('From MyAnimeList')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: discoverPage(http.get(api('/recommendations/reviews/:malId'), () => neverResolve())) },
}

export const UpstreamFailed: Story = {
  parameters: { msw: serve(null) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/MyAnimeList didn't respond/)).toBeVisible()
  },
}

export const NoReviews: Story = {
  parameters: { msw: serve([]) },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.queryByText('From MyAnimeList')).toBeNull())
  },
}

export const NoMalId: Story = {
  args: { malId: null },
}

export const List: StoryObj = {
  render: () => <ReviewList reviews={reviews} isLoading={false} />,
  parameters: { layout: 'padded' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('mapmaker99')).toBeVisible()
  },
}

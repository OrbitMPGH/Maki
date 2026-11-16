import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import type { SeriesReviews } from '../../api/hooks'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { discoverPage, quickTakes, reviews, seriesReviews } from '../../../.storybook/mocks/discover'
import { DiscoverReviews, ReviewList } from './DiscoverReviews'

const meta = {
  component: DiscoverReviews,
  tags: ['ai-generated'],
  args: { ids: { malId: 5001, aniListId: 7001, kitsuId: 9001 } },
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

const serve = (body: SeriesReviews) =>
  discoverPage(http.get(api('/recommendations/reviews'), () => HttpResponse.json(body)))

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('quietpageturner')).toBeVisible()
    await expect(await canvas.findByText('A slow burn that earns its ending')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('img', { name: 'From AniList' })).toHaveLength(2))
    await waitFor(() => expect(canvas.getAllByRole('img', { name: 'From MyAnimeList' })).toHaveLength(2))
    await expect(await canvas.findByText('Quick takes')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('img', { name: 'From Kitsu' })).toHaveLength(2))
    await expect(await canvas.findByRole('img', { name: '214 readers found this helpful' })).toBeVisible()
    await expect(await canvas.findByRole('img', { name: '1 reader found this helpful' })).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: discoverPage(http.get(api('/recommendations/reviews'), () => neverResolve())) },
}

export const OneSiteDidNotAnswer: Story = {
  parameters: { msw: serve({ reviews: reviews.filter((r) => r.source === 'anilist'), quickTakes, unavailable: ['myanimelist'] }) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/MyAnimeList didn't respond/)).toBeVisible()
    await expect(await canvas.findByText('lanternlit')).toBeVisible()
  },
}

export const NoSiteAnswered: Story = {
  parameters: { msw: serve({ reviews: [], quickTakes: [], unavailable: ['myanimelist', 'anilist', 'kitsu'] }) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Kitsu didn't respond/)).toBeVisible()
  },
}

export const RequestFailed: Story = {
  parameters: { msw: discoverPage(http.get(api('/recommendations/reviews'), () => failWith(502, 'Bad gateway'))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Reviews couldn't be loaded right now.")).toBeVisible()
  },
}

export const NoReviews: Story = {
  parameters: { msw: serve({ reviews: [], quickTakes: [], unavailable: [] }) },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.queryByText('Reviews')).toBeNull())
  },
}

export const OnlyQuickTakes: Story = {
  parameters: { msw: serve({ reviews: [], quickTakes, unavailable: [] }) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Comfort reading with teeth.')).toBeVisible()
  },
}

export const NoIds: Story = {
  args: { ids: { malId: null, aniListId: null, kitsuId: null } },
}

export const List: StoryObj = {
  render: () => <ReviewList data={seriesReviews} isLoading={false} />,
  parameters: { layout: 'padded' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('mapmaker99')).toBeVisible()
  },
}

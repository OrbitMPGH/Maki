import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import type { MangaReview } from '../../api/hooks'
import { api, daysAgo, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { SeriesReviews } from './SeriesReviews'

const reviews: MangaReview[] = [
  {
    author: 'quietlantern',
    score: 9,
    text: 'The pacing of the winter arcs is the best thing the series does. Every chapter earns the next one, and the lamplight art carries scenes that would be filler elsewhere.',
    url: 'https://myanimelist.example/reviews/1',
    date: daysAgo(40),
    tags: ['Recommended'],
  },
  {
    author: 'pagecounter',
    score: 6,
    text: 'Gorgeous, but the middle volumes repeat the caravan structure a few times too often. Worth reading for the ending.',
    url: null,
    date: daysAgo(120),
    tags: ['Mixed Feelings'],
  },
  {
    author: 'noscore',
    score: null,
    text: 'Short take: read it.',
    url: null,
    date: null,
    tags: [],
  },
]

const reviewsFor = (body: () => Response | Promise<Response>) => [http.get(api('/recommendations/reviews/:malId'), body)]

const meta = {
  component: SeriesReviews,
  tags: ['ai-generated'],
  args: { malId: 10201 },
  parameters: { msw: reviewsFor(() => HttpResponse.json(reviews)) },
} satisfies Meta<typeof SeriesReviews>

export default meta
type Story = StoryObj<typeof meta>

export const OpensDrawerWithReviews: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reviews' }))
    const drawer = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    const reviewer = await within(drawer).findByText('quietlantern')
    // The drawer is still sliding in when its content first renders.
    await waitFor(() => expect(reviewer).toBeVisible())
    await waitFor(() => expect(within(drawer).getByRole('link', { name: /read more on myanimelist/i })).toHaveAttribute(
      'href',
      'https://myanimelist.net/manga/10201/_/reviews',
    ))
  },
}

export const NoReviews: Story = {
  parameters: { msw: reviewsFor(() => HttpResponse.json([])) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reviews' }))
    const empty = await within(canvasElement.ownerDocument.body).findByText('No reviews on MyAnimeList yet.')
    await waitFor(() => expect(empty).toBeVisible())
  },
}

export const Loading: Story = {
  parameters: { msw: reviewsFor(() => neverResolve()) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reviews' }))
  },
}

export const UpstreamFailed: Story = {
  // `null` is how the server says MyAnimeList itself did not answer.
  parameters: { msw: reviewsFor(() => HttpResponse.json(null)) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reviews' }))
  },
}

export const ServerError: Story = {
  parameters: { msw: reviewsFor(() => failWith(502, 'MyAnimeList is not answering')) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reviews' }))
  },
}

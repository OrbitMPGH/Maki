import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import type { SeriesReviews as SeriesReviewsData } from '../../api/hooks'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { quickTakes, reviews } from '../../../.storybook/mocks/discover'
import { SeriesReviews } from './SeriesReviews'

const data: SeriesReviewsData = { reviews, quickTakes, unavailable: [] }

const reviewsFor = (body: () => Response | Promise<Response>) => [http.get(api('/recommendations/reviews'), body)]

const meta = {
  component: SeriesReviews,
  tags: ['ai-generated'],
  args: { ids: { malId: 10201, aniListId: 20201, kitsuId: 30201 } },
  parameters: { msw: reviewsFor(() => HttpResponse.json(data)) },
} satisfies Meta<typeof SeriesReviews>

export default meta
type Story = StoryObj<typeof meta>

type PlayContext = Parameters<NonNullable<Story['play']>>[0]

const openDrawer = async ({ canvas, canvasElement, userEvent }: PlayContext) => {
  await userEvent.click(await canvas.findByRole('button', { name: 'Reviews' }))
  return within(canvasElement.ownerDocument.body).findByRole('dialog')
}

export const OpensDrawerWithReviews: Story = {
  play: async (context) => {
    const drawer = await openDrawer(context)
    const reviewer = await within(drawer).findByText('quietpageturner')
    // The drawer is still sliding in when its content first renders.
    await waitFor(() => expect(reviewer).toBeVisible())
    await waitFor(() => expect(within(drawer).getAllByRole('img', { name: 'From AniList' })).toHaveLength(2))
    await waitFor(() => expect(within(drawer).getByText('Quick takes')).toBeVisible())
    await waitFor(() =>
      expect(within(drawer).getByRole('link', { name: /read more on myanimelist/i })).toHaveAttribute(
        'href',
        'https://myanimelist.net/manga/10201/_/reviews',
      ),
    )
    await waitFor(() =>
      expect(within(drawer).getByRole('link', { name: /read more on anilist/i })).toHaveAttribute(
        'href',
        'https://anilist.co/manga/20201',
      ),
    )
  },
}

export const OnlyAniList: Story = {
  args: { ids: { malId: null, aniListId: 20201, kitsuId: null } },
  play: async (context) => {
    const drawer = await openDrawer(context)
    await waitFor(() => expect(within(drawer).getByRole('link', { name: /read more on anilist/i })).toBeVisible())
    await expect(within(drawer).queryByRole('link', { name: /read more on myanimelist/i })).toBeNull()
  },
}

export const NoReviews: Story = {
  parameters: { msw: reviewsFor(() => HttpResponse.json({ reviews: [], quickTakes: [], unavailable: [] })) },
  play: async (context) => {
    const drawer = await openDrawer(context)
    const empty = await within(drawer).findByText('Nobody has reviewed this yet.')
    await waitFor(() => expect(empty).toBeVisible())
  },
}

export const Loading: Story = {
  parameters: { msw: reviewsFor(() => neverResolve()) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Reviews' }))
  },
}

export const OneSiteDidNotAnswer: Story = {
  parameters: {
    msw: reviewsFor(() =>
      HttpResponse.json({ reviews: reviews.filter((r) => r.source === 'myanimelist'), quickTakes: [], unavailable: ['anilist', 'kitsu'] }),
    ),
  },
  play: async (context) => {
    const drawer = await openDrawer(context)
    const note = await within(drawer).findByText(/AniList didn't respond/)
    await waitFor(() => expect(note).toBeVisible())
  },
}

export const ServerError: Story = {
  parameters: { msw: reviewsFor(() => failWith(502, 'Bad gateway')) },
  play: async (context) => {
    const drawer = await openDrawer(context)
    const line = await within(drawer).findByText("Reviews couldn't be loaded right now.")
    await waitFor(() => expect(line).toBeVisible())
  },
}

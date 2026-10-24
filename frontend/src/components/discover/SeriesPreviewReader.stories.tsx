import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, cover, failWith } from '../../../.storybook/mocks/api'
import {
  discoverPage,
  previewFailed,
  previewFetching,
  previewReady,
} from '../../../.storybook/mocks/discover'
import { SeriesPreviewReader } from './SeriesPreviewReader'

const meta = {
  component: SeriesPreviewReader,
  tags: ['ai-generated'],
  args: {
    providerId: '30000',
    title: 'The Salt Cartographer',
    coverUrl: cover('The Salt Cartographer'),
    seriesType: 'manga',
    onClose: fn(),
  },
  parameters: { layout: 'fullscreen', msw: discoverPage() },
} satisfies Meta<typeof SeriesPreviewReader>

export default meta
type Story = StoryObj<typeof meta>

const body = (el: HTMLElement) => within(el.ownerDocument.body)

export const Ready: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const close = await body(canvasElement).findByRole('button', { name: 'Close preview' })
    await waitFor(() => expect(body(canvasElement).getByRole('radio', { name: 'Paged' })).toBeChecked())
    await userEvent.click(close)
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const ScrollMode: Story = {
  play: async ({ canvasElement, userEvent }) => {
    await userEvent.click(await body(canvasElement).findByText('Scroll'))
    await waitFor(() => expect(body(canvasElement).getByRole('radio', { name: 'Scroll' })).toBeChecked())
  },
}

export const StillFetching: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/preview/:id'), ({ params }) => HttpResponse.json(previewFetching(String(params.id)))),
    ),
  },
  play: async ({ canvasElement }) => {
    await expect(await body(canvasElement).findByRole('button', { name: 'Close preview' })).toBeVisible()
  },
}

export const Expired: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/preview/:id'), () => failWith(404, 'That preview has expired'))),
  },
}

export const NoSource: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/preview/:id'), ({ params }) => HttpResponse.json(previewFailed(String(params.id)))),
    ),
  },
}

export const ClaimedByReadingProfile: Story = {
  args: { seriesType: 'manhwa' },
  parameters: {
    msw: discoverPage(
      http.get(api('/readingprofiles'), () =>
        HttpResponse.json([
          {
            id: 1,
            name: 'Webtoon',
            seriesTypes: ['manhwa'],
            prefs: { mode: 'vertical' },
          },
        ]),
      ),
      http.get(api('/preview/:id'), ({ params }) => HttpResponse.json(previewReady(String(params.id)))),
    ),
  },
  play: async ({ canvasElement }) => {
    const scroll = await body(canvasElement).findByRole('radio', { name: 'Scroll' })
    await waitFor(() => expect(scroll).toBeChecked())
  },
}

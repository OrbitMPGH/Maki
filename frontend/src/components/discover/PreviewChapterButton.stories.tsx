import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../../.storybook/mocks/api'
import { discoverPage, previewFailed, previewFetching } from '../../../.storybook/mocks/discover'
import { PreviewChapterButton } from './PreviewChapterButton'

const meta = {
  component: PreviewChapterButton,
  tags: ['ai-generated'],
  args: { providerId: '30000', title: 'The Salt Cartographer', onRead: fn() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 320 }}>
        <Story />
      </div>
    ),
  ],
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof PreviewChapterButton>

export default meta
type Story = StoryObj<typeof meta>

export const Idle: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: 'Preview first chapter' })).toBeVisible()
  },
}

export const ReadyOpensReader: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Preview first chapter' }))
    await waitFor(() => expect(args.onRead).toHaveBeenCalledOnce())
  },
}

const fetching = discoverPage(
  http.post(api('/preview/:id'), ({ params }) => HttpResponse.json(previewFetching(String(params.id)))),
  http.get(api('/preview/:id'), ({ params }) => HttpResponse.json(previewFetching(String(params.id)))),
)

export const Downloading: Story = {
  parameters: { msw: fetching },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Preview first chapter' }))
    await expect(await canvas.findByText(/Downloading, 3 of 8 pages/)).toBeVisible()
    await expect(await canvas.findByText(/keep browsing while it downloads/)).toBeVisible()
  },
}

export const NoSourceFound: Story = {
  parameters: {
    msw: discoverPage(
      http.post(api('/preview/:id'), ({ params }) => HttpResponse.json(previewFailed(String(params.id)))),
      http.get(api('/preview/:id'), ({ params }) => HttpResponse.json(previewFailed(String(params.id)))),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Preview first chapter' }))
    await expect(await canvas.findByText('No source could serve the first chapter.')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Try the preview again' })).toBeVisible()
  },
}

export const StartRejected: Story = {
  parameters: {
    msw: discoverPage(http.post(api('/preview/:id'), () => failWith(503, 'Preview downloads are switched off'))),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Preview first chapter' }))
    await expect(await canvas.findByText('Preview downloads are switched off')).toBeVisible()
  },
}

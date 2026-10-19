import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { singleFailureGroup } from '../../.storybook/mocks/operations/queue'
import { FailedQueueActions } from './FailedQueueActions'

const meta = {
  component: FailedQueueActions,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof FailedQueueActions>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Retry failed' })).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Remove failed' })).toBeVisible()
  },
}

export const RetryNarrowedByReason: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Retry failed' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Retry failed downloads' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Reason' }))
    await userEvent.click(await body.findByRole('option', { name: 'The source returned no pages' }))
    await waitFor(() =>
      expect(within(dialog).getByText('4 failed downloads will be queued again, ignoring the automatic retry limit.')).toBeVisible(),
    )
  },
}

export const RemoveWithTorrents: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Remove failed' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Remove failed downloads' })
    await expect(within(dialog).getByRole('checkbox', { name: "Don't grab removed torrent releases again" })).toBeChecked()
    await waitFor(() => expect(within(dialog).getByText('8 failed downloads will be removed from the queue.')).toBeVisible())
  },
}

export const SingleReasonRetriesAtOnce: Story = {
  parameters: { msw: [http.get(api('/queue/failed'), () => HttpResponse.json(singleFailureGroup))] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Retry failed' }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText('Retrying 6 failed downloads')).toBeVisible())
  },
}

export const OnlyTorrentsFailed: Story = {
  parameters: {
    msw: [
      http.get(api('/queue/failed'), () =>
        HttpResponse.json([{ reasonKey: 'error.torrentImport.notInClient', sourceName: 'torrent', count: 2 }]),
      ),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Remove failed' })).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Retry failed' })).not.toBeInTheDocument()
  },
}

export const NothingFailed: Story = {
  parameters: { msw: [http.get(api('/queue/failed'), () => HttpResponse.json([]))] },
}

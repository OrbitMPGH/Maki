import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { singleFailureGroup } from '../../.storybook/mocks/operations/queue'
import { QueueActions } from './QueueActions'

const meta = {
  component: QueueActions,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof QueueActions>

export default meta
type Story = StoryObj<typeof meta>

const openMenu = async (
  canvas: { findByRole: (role: string, options: { name: string }) => Promise<HTMLElement> },
  userEvent: { click: (element: Element) => Promise<void> },
) => userEvent.click(await canvas.findByRole('button', { name: 'More queue actions' }))

export const Default: Story = {
  args: { onClearQueue: fn() },
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByRole('button', { name: 'Retry failed' })).toBeVisible()
    await openMenu(canvas, userEvent)
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('menuitem', { name: 'Remove failed' })).toBeVisible()
    await userEvent.click(await body.findByRole('menuitem', { name: 'Clear queue' }))
    await waitFor(() => expect(args.onClearQueue).toHaveBeenCalled())
  },
}

export const RetryNarrowedByReason: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Retry failed' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Retry failed downloads' })
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Reason' }))
    await userEvent.click(await body.findByRole('option', { name: 'The source returned no pages' }))
    await waitFor(() =>
      expect(within(dialog).getByText('4 failed downloads will be queued again, ignoring the automatic retry limit.')).toBeVisible(),
    )
  },
}

export const RemoveWithTorrents: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await openMenu(canvas, userEvent)
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('menuitem', { name: 'Remove failed' }))
    const dialog = await body.findByRole('dialog', { name: 'Remove failed downloads' })
    await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: "Don't grab removed torrent releases again" })).toBeChecked())
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
  play: async ({ canvas, canvasElement, userEvent }) => {
    await openMenu(canvas, userEvent)
    await expect(await within(canvasElement.ownerDocument.body).findByRole('menuitem', { name: 'Remove failed' })).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Retry failed' })).not.toBeInTheDocument()
  },
}

export const NothingFailed: Story = {
  parameters: { msw: [http.get(api('/queue/failed'), () => HttpResponse.json([]))] },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'More queue actions' })).toBeNull())
  },
}

export const NothingFailedCanStillClear: Story = {
  args: { onClearQueue: fn() },
  parameters: { msw: [http.get(api('/queue/failed'), () => HttpResponse.json([]))] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await openMenu(canvas, userEvent)
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('menuitem', { name: 'Clear queue' })).toBeVisible()
    await expect(body.queryByRole('menuitem', { name: 'Remove failed' })).toBeNull()
  },
}

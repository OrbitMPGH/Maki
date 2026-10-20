import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { awaitingImportItem, guardedItem, importPlan } from '../../.storybook/mocks/operations/queue'
import { ImportReviewModal } from './ImportReviewModal'

const meta = {
  component: ImportReviewModal,
  tags: ['ai-generated'],
  args: { queueItemId: awaitingImportItem.id, onClose: fn() },
} satisfies Meta<typeof ImportReviewModal>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review import' })
    await waitFor(async () => expect(await within(dialog).findByText('Clockwork Tea House v01.cbz')).toBeVisible())
    await waitFor(() => expect(within(dialog).getByText(/replaces Ch.1-4.cbz/)).toBeVisible())
    await waitFor(() => expect(within(dialog).getByRole('button', { name: /Import everything, replace the 3 files it covers/ })).toBeVisible())
  },
}

export const IgnoreDownload: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review import' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Ignore this download' }))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const UpgradeWithSkips: Story = {
  args: { queueItemId: guardedItem.id },
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review import' })
    await waitFor(async () => expect(await within(dialog).findByText('Held back before importing')).toBeVisible())
    const skips = await within(dialog).findAllByRole('checkbox', { name: 'Skip this file' })
    await waitFor(() => expect(skips[1]).toBeChecked())
    await userEvent.click(skips[0])
    await waitFor(() => expect(within(dialog).getByText('2 files will be left out of the import')).toBeVisible())
  },
}

export const UnreadableDownload: Story = {
  parameters: {
    msw: [
      http.get(api('/queue/:id/import-plan'), ({ params }) =>
        HttpResponse.json(
          importPlan(Number(params.id), { error: 'The download folder is empty or no longer reachable.', files: [] }),
        ),
      ),
    ],
  },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText("Can't read this download")).toBeVisible())
    await waitFor(() => expect(body.getByRole('button', { name: 'Discard download' })).toBeVisible())
  },
}

export const LoadFailed: Story = {
  parameters: { msw: [http.get(api('/queue/:id/import-plan'), () => failWith(500, 'The plan could not be built'))] },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText("Can't load this import")).toBeVisible())
    await waitFor(() => expect(body.getByRole('button', { name: 'Retry' })).toBeVisible())
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/queue/:id/import-plan'), () => neverResolve())] },
  play: async ({ canvasElement }) => {
    await waitFor(async () => expect(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Review import' })).toBeVisible())
  },
}

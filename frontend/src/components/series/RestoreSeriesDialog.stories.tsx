import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { RestoreSeriesDialog } from './RestoreSeriesDialog'

const meta = {
  component: RestoreSeriesDialog,
  tags: ['ai-generated'],
  args: {
    opened: true,
    onClose: fn(),
    onRestore: fn(),
    removedCount: 142,
  },
} satisfies Meta<typeof RestoreSeriesDialog>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByText('Download the 142 removed chapters again?')).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Restore and download' }))
    await waitFor(() => expect(args.onRestore).toHaveBeenCalledWith(true))
  },
}

export const RestoreOnly: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Restore only' }))
    await waitFor(() => expect(args.onRestore).toHaveBeenCalledWith(false))
  },
}

export const OneChapter: Story = {
  args: { removedCount: 1 },
  play: async ({ canvasElement }) => {
    await expect(
      await within(canvasElement.ownerDocument.body).findByText('Download the 1 removed chapter again?'),
    ).toBeVisible()
  },
}

export const WithoutDownloadPermission: Story = {
  args: { canDownload: false },
  play: async ({ canvasElement }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByRole('button', { name: 'Restore only' })).toBeVisible()
    await expect(within(dialog).queryByRole('button', { name: 'Restore and download' })).toBeNull()
  },
}

export const Restoring: Story = {
  args: { loading: true },
}

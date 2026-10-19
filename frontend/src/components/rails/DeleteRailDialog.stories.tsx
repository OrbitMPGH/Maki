import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { DeleteRailDialog } from './DeleteRailDialog'

const meta = {
  component: DeleteRailDialog,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn(), onDeleted: fn(), railId: 3, railName: 'Top rated romance' },
} satisfies Meta<typeof DeleteRailDialog>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Delete rail' })
    await expect(within(dialog).getByText(/Delete the rail "Top rated romance"/)).toBeVisible()
  },
}

export const ConfirmDeletes: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Delete rail' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(args.onDeleted).toHaveBeenCalledOnce())
    await expect(args.onClose).toHaveBeenCalled()
    await expect(await body.findByText('Rail deleted')).toBeVisible()
  },
}

export const Cancel: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Delete rail' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }))
    await expect(args.onClose).toHaveBeenCalledOnce()
    await expect(args.onDeleted).not.toHaveBeenCalled()
  },
}

export const Closed: Story = {
  args: { opened: false },
}

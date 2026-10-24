import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { ConfirmDialog } from './ConfirmDialog'

const meta = {
  component: ConfirmDialog,
  tags: ['ai-generated'],
  args: {
    opened: true,
    onClose: fn(),
    onConfirm: fn(),
    title: 'Delete tag',
    children: 'Removes "Favourites" from 14 series. The series themselves stay in your library.',
    confirmLabel: 'Delete tag',
  },
} satisfies Meta<typeof ConfirmDialog>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    // Mantine renders the modal in a portal on document.body, outside the story canvas.
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Delete tag' })
    await waitFor(() => expect(dialog).toBeVisible())
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Delete tag' }))
    await expect(args.onConfirm).toHaveBeenCalledOnce()
  },
}

export const Loading: Story = {
  args: { loading: true },
}

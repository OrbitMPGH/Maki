import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { ArchiveHint } from './ArchiveHint'

const meta = {
  component: ArchiveHint,
  tags: ['ai-generated'],
  args: { onArchive: fn(), onDismiss: fn() },
} satisfies Meta<typeof ArchiveHint>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText("You've finished this. Archive it?")).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Archive' }))
    await waitFor(() => expect(args.onArchive).toHaveBeenCalledOnce())
  },
}

export const Dismiss: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Dismiss' }))
    await waitFor(() => expect(args.onDismiss).toHaveBeenCalledOnce())
  },
}

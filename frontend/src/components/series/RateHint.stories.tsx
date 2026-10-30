import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { RateHint } from './RateHint'

const meta = {
  component: RateHint,
  tags: ['ai-generated'],
  args: { onRate: fn(), onDismiss: fn() },
} satisfies Meta<typeof RateHint>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("You've finished this. How was it?")).toBeVisible()
    await expect(canvas.getByRole('group', { name: 'Rate this series' })).toBeVisible()
  },
}

export const Dismiss: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Dismiss' }))
    await waitFor(() => expect(args.onDismiss).toHaveBeenCalledOnce())
  },
}

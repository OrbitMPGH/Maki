import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { RatingCheckpointPrompt } from './RatingCheckpointPrompt'

const meta = {
  component: RatingCheckpointPrompt,
  tags: ['ai-generated'],
  args: { checkpoint: 'hooked', onRate: fn(), onDismiss: fn() },
} satisfies Meta<typeof RatingCheckpointPrompt>

export default meta
type Story = StoryObj<typeof meta>

export const Hooked: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('How is it going so far?')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: /Still a/ })).toBeNull()
  },
}

export const CaughtUpKeepsTheScore: Story = {
  args: { checkpoint: 'caughtUp', soFar: 7 },
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText("You're caught up. How is it so far?")).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Still a 7' }))
    await waitFor(() => expect(args.onRate).toHaveBeenCalledWith(7))
  },
}

export const Dismiss: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Dismiss' }))
    await waitFor(() => expect(args.onDismiss).toHaveBeenCalledOnce())
  },
}

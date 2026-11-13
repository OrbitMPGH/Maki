import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { CompleteHint } from './CompleteHint'

const meta = {
  component: CompleteHint,
  tags: ['ai-generated'],
  args: { leftOut: 3, onComplete: fn(), onDismiss: fn() },
} satisfies Meta<typeof CompleteHint>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(
      await canvas.findByText("You've read every chapter you kept. 3 chapters were left out. Done with this series?"),
    ).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Mark completed' }))
    await waitFor(() => expect(args.onComplete).toHaveBeenCalledOnce())
  },
}

export const NotYet: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Not yet' }))
    await waitFor(() => expect(args.onDismiss).toHaveBeenCalledOnce())
  },
}

export const NothingLeftOut: Story = {
  args: { leftOut: 0 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("You've read every chapter. Done with this series?")).toBeVisible()
  },
}

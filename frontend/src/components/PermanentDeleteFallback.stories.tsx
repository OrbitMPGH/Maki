import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { PermanentDeleteFallback } from './PermanentDeleteFallback'

const meta = {
  component: PermanentDeleteFallback,
  tags: ['ai-generated'],
  args: { onConfirm: fn() },
} satisfies Meta<typeof PermanentDeleteFallback>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: /delete permanently/i }))
    await expect(args.onConfirm).toHaveBeenCalledOnce()
  },
}

export const Loading: Story = {
  args: { loading: true },
}

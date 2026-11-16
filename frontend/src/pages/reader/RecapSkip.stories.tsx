import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent } from 'storybook/test'
import RecapSkip from './RecapSkip'

const meta = {
  component: RecapSkip,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: { phase: 'offer', onSkip: fn() },
  decorators: [
    (Story) => (
      <div className="reader-root">
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof RecapSkip>

export default meta
type Story = StoryObj<typeof meta>

export const Offer: Story = {
  play: async ({ canvas, args }) => {
    const button = await canvas.findByRole('button', { name: 'Skip recap' })
    await userEvent.click(button)
    await expect(args.onSkip).toHaveBeenCalledOnce()
  },
}

/** After an automatic skip: a note, not a button, that the reader clears after a moment. */
export const Skipped: Story = {
  args: { phase: 'skipped' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('status')).toHaveTextContent('Recap skipped')
    await expect(canvas.queryByRole('button')).toBeNull()
  },
}

export const Gone: Story = {
  args: { phase: null },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('.reader-recap-skip')).toBeNull()
  },
}

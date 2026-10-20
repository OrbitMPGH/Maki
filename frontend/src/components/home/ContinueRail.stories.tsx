import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { continueReading, jumpBackIn } from '../../../.storybook/mocks/home'
import { ContinueRail } from './ContinueRail'

const meta = {
  component: ContinueRail,
  tags: ['ai-generated'],
  args: { items: continueReading, rail: 'continue' },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof ContinueRail>

export default meta
type Story = StoryObj<typeof meta>

export const Continue: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByRole('button', { name: /more options for/i })).toHaveLength(continueReading.length))
  },
}

export const JumpBackIn: Story = {
  args: { items: jumpBackIn, rail: 'jumpback' },
}

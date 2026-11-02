import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { continueReading, notStarted } from '../../../.storybook/mocks/home'
import { ContinueRail } from './ContinueRail'

const meta = {
  component: ContinueRail,
  tags: ['ai-generated'],
  args: { items: continueReading },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof ContinueRail>

export default meta
type Story = StoryObj<typeof meta>

export const Continue: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByRole('button', { name: /more options for/i })).toHaveLength(continueReading.length))
  },
}

export const BetweenChapters: Story = {
  args: { items: notStarted },
}

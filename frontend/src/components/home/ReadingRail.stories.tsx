import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { continueReading, notStarted } from '../../../.storybook/mocks/home'
import { ReadingRail } from './ReadingRail'

const meta = {
  component: ReadingRail,
  tags: ['ai-generated'],
  args: { items: continueReading },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof ReadingRail>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('link', { name: /Lanterns Over Kasumi/ })).toHaveAttribute('href', '/read/1132'))
    await waitFor(() => expect(canvas.getAllByRole('button', { name: /more options for/i })).toHaveLength(continueReading.length))
  },
}

export const NotStartedAndNoCover: Story = {
  args: { items: notStarted },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByText('Start')).toHaveLength(notStarted.length))
  },
}

export const SingleItem: Story = {
  args: { items: continueReading.slice(0, 1) },
}

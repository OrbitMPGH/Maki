import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { continueReading, jumpBackIn } from '../../../.storybook/mocks/home'
import { ReadingRail } from './ReadingRail'

const meta = {
  component: ReadingRail,
  tags: ['ai-generated'],
  args: { items: continueReading, rail: 'continue' },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof ReadingRail>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('link', { name: /Lanterns Over Kasumi/ })).toHaveAttribute('href', '/read/1132')
    await expect(canvas.getAllByRole('button', { name: /more options for/i })).toHaveLength(continueReading.length)
  },
}

export const NotStartedAndNoCover: Story = {
  args: { items: jumpBackIn, rail: 'jumpback' },
  play: async ({ canvas }) => {
    await expect(canvas.getAllByText('Start')).toHaveLength(jumpBackIn.length)
  },
}

export const SingleItem: Story = {
  args: { items: continueReading.slice(0, 1) },
}

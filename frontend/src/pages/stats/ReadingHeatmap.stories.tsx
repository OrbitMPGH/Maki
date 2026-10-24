import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { daysAgo } from '../../../.storybook/mocks/api'
import { heatmapDays } from '../../../.storybook/mocks/insights'
import { ReadingHeatmap } from './ReadingHeatmap'

const meta = {
  component: ReadingHeatmap,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: { days: heatmapDays },
} satisfies Meta<typeof ReadingHeatmap>

export default meta
type Story = StoryObj<typeof meta>

export const Year: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Reading days' })).toBeVisible()
    await expect(await canvas.findByText('Less')).toBeVisible()
  },
}

export const NewReader: Story = {
  args: {
    days: [
      { date: daysAgo(2).slice(0, 10), chapters: 3, seconds: 1100 },
      { date: daysAgo(1).slice(0, 10), chapters: 1, seconds: 400 },
    ],
  },
}

export const NothingYet: Story = { args: { days: [] } }

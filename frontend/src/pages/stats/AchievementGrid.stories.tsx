import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { achievements } from '../../../.storybook/mocks/insights'
import { AchievementGrid } from './AchievementGrid'

const meta = {
  component: AchievementGrid,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: { achievements },
} satisfies Meta<typeof AchievementGrid>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('heading', { name: 'Reading' })).toBeVisible()
    await expect(canvas.getByRole('heading', { name: 'The library' })).toBeVisible()
    await expect(canvas.getAllByText('Gold').length).toBe(2)
  },
}

export const ReaderBadgesOnly: Story = {
  args: { achievements: achievements.filter((a) => a.track === 'Reader') },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('heading', { name: 'The library' })).toBeNull()
  },
}

/** A new reader: everything locked, nothing graded past zero. */
export const AllLocked: Story = {
  args: {
    achievements: achievements.map((a) => ({
      ...a,
      tier: 0,
      tierName: null,
      value: 0,
      nextThreshold: a.tiers[0],
      unlockedAt: null,
    })),
  },
}

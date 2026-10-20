import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { Paper } from '@mantine/core'
import { progressSummary } from '../../../.storybook/mocks/home'
import { ProgressCard } from './ProgressCard'

const meta = {
  component: ProgressCard,
  tags: ['ai-generated'],
  args: { summary: progressSummary },
  parameters: { layout: 'padded' },
  // The card has no frame of its own: Home's glance row supplies the border and padding.
  decorators: [
    (Story) => (
      <div className="home-glance">
        <Paper withBorder radius="lg" className="home-glance-panel home-glance-wide">
          <Story />
        </Paper>
      </div>
    ),
  ],
} satisfies Meta<typeof ProgressCard>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Level 12')).toBeVisible()
    await expect(await canvas.findByText('Day streak')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('link')).toHaveAttribute('href', '/stats?section=progress'))
  },
}

export const StreaksHidden: Story = {
  args: { summary: { ...progressSummary, showStreaks: false } },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText('Day streak')).toBeNull()
  },
}

export const NewReader: Story = {
  args: {
    summary: {
      ...progressSummary,
      level: { level: 1, xp: 20, intoLevel: 20, levelSpan: 100, nextLevelXp: 100, progress: 0.2 },
      chaptersRead: 3,
      readingSeconds: 640,
      currentStreak: 1,
      longestStreak: 1,
      earned: 0,
    },
  },
}

export const HighLevel: Story = {
  args: {
    summary: {
      ...progressSummary,
      level: { level: 87, xp: 481_200, intoLevel: 6_100, levelSpan: 6_400, nextLevelXp: 487_600, progress: 0.95 },
      chaptersRead: 48_120,
      readingSeconds: 9_400_000,
      currentStreak: 212,
      longestStreak: 340,
      earned: 37,
    },
  },
}

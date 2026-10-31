import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import type { HeatmapDay } from '../../api/hooks'
import { continueReading, progressSummary } from '../../../.storybook/mocks/home'
import { ContinueHero } from './ContinueHero'
import { ReadingWeeks } from './ReadingWeeks'

// Local dates, the way the server buckets them for the reader's own time zone.
const localDay = (back: number) => {
  const d = new Date()
  d.setDate(d.getDate() - back)
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
}
const pattern = [0, 9, 14, 2, 5, 0, 3, 12, 0, 21, 5, 3, 0, 7, 1, 0, 9, 4, 6, 0, 2, 11, 3, 0, 8, 5, 2, 0, 4, 9, 1, 6, 0, 3, 7]
const history = (readToday: boolean): HeatmapDay[] =>
  pattern
    .map((chapters, back) => ({ date: localDay(back), chapters: back === 0 ? (readToday ? 4 : 0) : chapters, seconds: 0 }))
    .map((d) => ({ ...d, seconds: d.chapters * 340 }))
    .filter((d) => d.chapters > 0)

const meta = {
  component: ReadingWeeks,
  tags: ['ai-generated'],
  args: { days: history(false), summary: progressSummary },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof ReadingWeeks>

export default meta
type Story = StoryObj<typeof meta>

/** In the Continue hero, which is where Home puts it: a right-hand column when wide, a row under the actions otherwise. */
export const InTheHero: Story = {
  args: { hosted: true },
  render: (args) => <ContinueHero items={continueReading} glance={<ReadingWeeks {...args} />} />,
  play: async ({ canvas }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(within(glance).getByText('Read today to keep it going')).toBeVisible()
    await expect(within(glance).getByLabelText('6 day streak')).toBeVisible()
    await waitFor(() => expect(within(glance).getByRole('progressbar', { name: 'This week' })).toHaveAttribute('aria-valuenow', '14'))
  },
}

export const ReadToday: Story = {
  args: { days: history(true), hosted: true },
  render: (args) => <ContinueHero items={continueReading} glance={<ReadingWeeks {...args} />} />,
  play: async ({ canvas }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(within(glance).getByText('Today counts. Best: 31 days')).toBeVisible()
  },
}

/** At its own slot, when the layout puts it away from the hero. */
export const OnItsOwn: Story = {
  play: async ({ canvas }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(glance).not.toHaveAttribute('data-hosted')
    await expect(within(glance).getByText('Days read')).toBeVisible()
  },
}

export const NewReader: Story = {
  args: { days: [], summary: { ...progressSummary, currentStreak: 0, longestStreak: 0, goals: [] } },
  play: async ({ canvas }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(within(glance).getByText('Read a chapter to start a streak')).toBeVisible()
    await expect(within(glance).queryByRole('progressbar')).toBeNull()
  },
}

export const StreaksSwitchedOff: Story = {
  args: { summary: { ...progressSummary, showStreaks: false } },
  play: async ({ canvas }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(within(glance).queryByLabelText(/day streak/)).toBeNull()
    await expect(within(glance).queryByText('Read today to keep it going')).toBeNull()
  },
}

export const GoalMet: Story = {
  args: { summary: { ...progressSummary, goals: [{ id: 1, period: 'Week', metric: 'Minutes', target: 120, progress: 150 }] } },
  play: async ({ canvas }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(within(glance).getByText('120 minutes', { exact: false })).toBeVisible()
  },
}

/** A month goal does not fit a five-week window, so the card leaves it to Stats. */
export const OnlyAMonthGoal: Story = {
  args: { summary: { ...progressSummary, goals: [{ id: 1, period: 'Month', metric: 'Chapters', target: 120, progress: 80 }] } },
  play: async ({ canvas }) => {
    const glance = await canvas.findByRole('region', { name: 'Last 5 weeks' })
    await expect(within(glance).queryByRole('progressbar')).toBeNull()
  },
}

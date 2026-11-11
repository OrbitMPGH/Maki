import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { seriesReadingStats } from '../../../.storybook/mocks/series'
import { SeriesReadingPanel } from './SeriesReadingPanel'

const estimate = {
  seconds: 20_640,
  remainingChapters: 43,
  style: 'paged' as const,
  sampleChapters: 88,
  seriesSpecific: true,
}

const meta = {
  component: SeriesReadingPanel,
  tags: ['ai-generated'],
  args: { stats: seriesReadingStats, estimate },
  decorators: [(Story) => <div style={{ maxWidth: 920 }}><Story /></div>],
} satisfies Meta<typeof SeriesReadingPanel>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Your reading' })).toBeVisible()
    await expect(canvas.getByText('11h 44m')).toBeVisible()
    await expect(canvas.getByText('across 37 sittings')).toBeVisible()
    await expect(canvas.getByText('About 5h 44m')).toBeVisible()
    await expect(canvas.getByRole('img', { name: 'Time per week' })).toBeVisible()
  },
}

export const CaughtUp: Story = {
  args: { estimate: null },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('11h 44m')).toBeVisible()
    await expect(canvas.queryByText('Time to finish')).toBeNull()
  },
}

/** A fully incognito series keeps its time on the chapters but never reaches the activity log. */
export const NoTimeline: Story = {
  args: {
    stats: { ...seriesReadingStats, sittings: 0, timeline: [], chapterMedianSeconds: null },
    estimate: null,
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('over 88 chapters')).toBeVisible()
    await expect(canvas.queryByRole('img', { name: 'Time per week' })).toBeNull()
    await expect(canvas.queryByText('Per chapter')).toBeNull()
  },
}

export const MonthlyHistory: Story = {
  args: {
    stats: {
      ...seriesReadingStats,
      bucketUnit: 'month',
      timeline: ['2025-12', '2026-01', '2026-02', '2026-03', '2026-04', '2026-05', '2026-06', '2026-07', '2026-08', '2026-09', '2026-10', '2026-11'].map(
        (bucket, i) => ({ bucket, seconds: [0, 3600, 7200, 5400, 0, 0, 1800, 9000, 12000, 6000, 4000, 2400][i] }),
      ),
    },
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Time per month' })).toBeVisible()
    await expect(canvas.getByText('This month: 40m')).toBeVisible()
  },
}

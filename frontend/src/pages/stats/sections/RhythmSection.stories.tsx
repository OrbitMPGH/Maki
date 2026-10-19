import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http } from 'msw'
import '@mantine/charts/styles.css'
import { api, neverResolve } from '../../../../.storybook/mocks/api'
import {
  activityHandler,
  insightsHandler,
  progressSummaryHandler,
  progressSummaryOff,
  progressSummaryRich,
  sectionProps,
  standingHandler,
} from '../../../../.storybook/mocks/insights'
import RhythmSection from './RhythmSection'

const meta = {
  component: RhythmSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: [progressSummaryHandler(progressSummaryRich)] },
  decorators: [
    (Story) => (
      <div className="stats-layout">
        <Story />
      </div>
    ),
  ],
  args: sectionProps,
} satisfies Meta<typeof RhythmSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Hour of day and weekday')).toBeVisible()
    await expect(await canvas.findByText('Reading days')).toBeVisible()
    await expect(canvas.getByText('Saturday')).toBeVisible()
  },
}

/** With progress tracking off there is no heatmap, only the bookmarks panel. */
export const WithoutHeatmap: Story = {
  parameters: { msw: [progressSummaryHandler(progressSummaryOff)] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Bookmarks')).toBeVisible()
    await expect(canvas.queryByText('Reading days')).toBeNull()
  },
}

/** Chapters marked read from Kavita or OPDS carry no time, so there is no rhythm to draw. */
export const NoTimedReading: Story = {
  parameters: { msw: [progressSummaryHandler(progressSummaryRich), insightsHandler('sparse'), standingHandler('sparse')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No timed reading yet')).toBeVisible()
  },
}

export const ReadTrackingOff: Story = {
  parameters: { msw: [progressSummaryHandler(progressSummaryRich), activityHandler('untracked')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No reading tracked yet')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [progressSummaryHandler(progressSummaryRich), http.get(api('/stats/insights'), () => neverResolve())] },
}

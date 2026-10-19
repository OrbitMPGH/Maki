import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http } from 'msw'
import { api, neverResolve } from '../../../../.storybook/mocks/api'
import { activityHandler, insightsHandler, sectionProps, standingHandler } from '../../../../.storybook/mocks/insights'
import TasteSection from './TasteSection'

const meta = {
  component: TasteSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <div className="stats-layout">
        <Story />
      </div>
    ),
  ],
  args: sectionProps,
} satisfies Meta<typeof TasteSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Lean')).toBeVisible()
    await expect(await canvas.findByText('You vs the crowd')).toBeVisible()
    await expect(await canvas.findByText('Creators you return to')).toBeVisible()
    await expect(await canvas.findByText('Found Family')).toBeVisible()
    await expect(await canvas.findByText(/You rate 0.6/)).toBeVisible()
  },
}

/** A new reader has no genre history, no ratings and no repeat creators yet. */
export const NewReader: Story = {
  parameters: { msw: [activityHandler('sparse'), insightsHandler('sparse'), standingHandler('sparse')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Not enough genre data yet')).toBeVisible()
    await expect(await canvas.findByText('No repeat creators yet')).toBeVisible()
    await expect(await canvas.findByText('Rate a few series to compare yourself with the crowd.')).toBeVisible()
  },
}

export const ReadTrackingOff: Story = {
  parameters: { msw: [activityHandler('untracked')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No reading tracked yet')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: {
    msw: [
      http.get(api('/stats/activity'), () => neverResolve()),
      http.get(api('/stats/insights'), () => neverResolve()),
      http.get(api('/stats/standing'), () => neverResolve()),
    ],
  },
}

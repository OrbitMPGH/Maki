import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http } from 'msw'
import '@mantine/charts/styles.css'
import { api, neverResolve } from '../../../../.storybook/mocks/api'
import { activityHandler, insightsHandler, sectionProps, standingHandler } from '../../../../.storybook/mocks/insights'
import ReadingSection from './ReadingSection'

const meta = {
  component: ReadingSection,
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
} satisfies Meta<typeof ReadingSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('Day by day')).toBeVisible()
    await expect(await canvas.findByText('Most read')).toBeVisible()
    await expect(await canvas.findByText('Activity feed')).toBeVisible()
    await userEvent.click(canvas.getByRole('radio', { name: 'Time' }))
    await expect(canvas.getByRole('radio', { name: 'Time' })).toBeChecked()
  },
}

/** All time has nothing before it, so the tiles show no change against a previous window. */
export const AllTime: Story = {
  args: { preset: 'all', previous: null, range: { from: '2024-01-01', to: '2026-10-19' }, windowLabel: 'all time' },
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByText('Day by day')).toBeVisible()
    await expect(canvasElement.querySelector('.stats-figure-delta')).toBeNull()
  },
}

export const NewReader: Story = {
  parameters: { msw: [activityHandler('sparse'), insightsHandler('sparse'), standingHandler('sparse')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Day by day')).toBeVisible()
    await expect(await canvas.findByText('Nothing sits half read right now.')).toBeVisible()
  },
}

export const ReadTrackingOff: Story = {
  parameters: { msw: [activityHandler('untracked')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No reading tracked yet')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/stats/activity'), () => neverResolve())] },
}

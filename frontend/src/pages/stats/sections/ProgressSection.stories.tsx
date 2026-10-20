import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../../.storybook/mocks/api'
import {
  leaderboard,
  progressSummaryHandler,
  progressSummaryNewcomer,
  progressSummaryOff,
  progressSummaryRich,
  sectionProps,
} from '../../../../.storybook/mocks/insights'
import ProgressSection from './ProgressSection'

const meta = {
  component: ProgressSection,
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
} satisfies Meta<typeof ProgressSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Level 12')).toBeVisible()
    await expect(await canvas.findByText('Goals')).toBeVisible()
    await expect(await canvas.findByText('Leaderboard')).toBeVisible()
    await expect(await canvas.findByText('Badges')).toBeVisible()
    await expect(await canvas.findByText(/Closest badge/)).toBeVisible()
  },
}

export const NewReader: Story = {
  parameters: {
    msw: [
      progressSummaryHandler(progressSummaryNewcomer),
      http.get(api('/progress/achievements'), () => HttpResponse.json([])),
      http.get(api('/progress/leaderboard'), () => HttpResponse.json(leaderboard.slice(0, 1))),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No goals set yet.')).toBeVisible()
    await expect(await canvas.findByText('No one else on this instance has opted in yet.')).toBeVisible()
    await expect(canvas.queryByText('Badges')).toBeNull()
  },
}

/** An admin looking at another reader sees no leaderboard: it answers about who opted in, not about them. */
export const ViewingAnotherReader: Story = {
  args: { userId: 2 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Level 12')).toBeVisible()
    await expect(canvas.queryByText('Leaderboard')).toBeNull()
  },
}

export const SwitchedOff: Story = {
  parameters: { msw: [progressSummaryHandler(progressSummaryOff)] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Progress tracking is switched off/)).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/progress/summary'), () => neverResolve())] },
}

/** A failed request is not reported as "switched off". */
export const LoadFailed: Story = {
  parameters: { msw: [http.get(api('/progress/summary'), () => failWith(500))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Could not load your progress/)).toBeVisible()
  },
}

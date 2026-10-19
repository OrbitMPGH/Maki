import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http } from 'msw'
import { api, neverResolve } from '../../../../.storybook/mocks/api'
import { activityHandler, sectionProps, standingHandler } from '../../../../.storybook/mocks/insights'
import HabitsSection from './HabitsSection'

const meta = {
  component: HabitsSection,
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
} satisfies Meta<typeof HabitsSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Finish rate')).toBeVisible()
    await expect(await canvas.findByText('Where you stop')).toBeVisible()
    await expect(await canvas.findByText('Savoured')).toBeVisible()
    await expect(await canvas.findByText('Backlog')).toBeVisible()
    await expect(canvas.getByText('Median')).toBeVisible()
  },
}

/** No unfinished series, no lists and no backlog: just the empty finish-rate ring. */
export const NewReader: Story = {
  parameters: { msw: [activityHandler('sparse'), standingHandler('sparse')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing unfinished to chart.')).toBeVisible()
    await expect(canvas.queryByText('Backlog')).toBeNull()
  },
}

export const ReadTrackingOff: Story = {
  parameters: { msw: [activityHandler('untracked')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No reading tracked yet')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/stats/standing'), () => neverResolve())] },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { activityFor, feedAdded, longFeed } from '../../../.storybook/mocks/insights'
import { ActivityFeed } from './ActivityFeed'

const rich = activityFor('2026-09-20', '2026-10-19', 'rich')

const meta = {
  component: ActivityFeed,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 720 }}>
        <Story />
      </div>
    ),
  ],
  args: { stats: rich },
} satisfies Meta<typeof ActivityFeed>

export default meta
type Story = StoryObj<typeof meta>

/** Finished, added, removed and stalled in one newest-first list; a series removed and re-added shows once. */
export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getAllByRole('link', { name: 'Iron Petal Academy' })).toHaveLength(1)
    await expect(canvas.getByText(/re-added 1×/)).toBeVisible()
    await expect(canvas.queryByRole('button', { name: /Show all/ })).toBeNull()
  },
}

export const LongFeed: Story = {
  args: { stats: longFeed },
  play: async ({ canvas, userEvent }) => {
    const more = canvas.getByRole('button', { name: 'Show all 30' })
    await userEvent.click(more)
    await expect(canvas.getByRole('button', { name: 'Show less' })).toBeVisible()
  },
}

export const OnlyAdditions: Story = {
  args: { stats: { ...rich, finished: [], removed: [], dropped: [], added: feedAdded.slice(0, 2) } },
}

/** With nothing to list the component renders nothing at all. */
export const Nothing: Story = {
  args: { stats: { ...rich, finished: [], added: [], removed: [], dropped: [] } },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('.mantine-Paper-root')).toBeNull()
  },
}

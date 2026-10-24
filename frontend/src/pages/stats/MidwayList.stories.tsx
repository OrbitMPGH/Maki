import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { cover, daysAgo } from '../../../.storybook/mocks/api'
import { seriesById } from '../../../.storybook/mocks/shared'
import { Panel } from '../../components/ui/Panel'
import { MidwayList } from './MidwayList'

const item = (id: number, read: number, held: number, etaSeconds: number | null) => ({
  seriesId: id,
  title: seriesById(id).title,
  coverUrl: cover(id),
  read,
  held,
  etaSeconds,
  lastReadAt: daysAgo(id),
})

const meta = {
  component: MidwayList,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md" style={{ maxWidth: 460 }}>
        <Story />
      </Panel>
    ),
  ],
  args: {
    emptyText: 'Nothing sits half read right now.',
    items: [item(3, 40, 48, 20_160), item(6, 150, 53, 22_260), item(11, 20, 37, null), item(7, 12, 108, 45_360)],
  },
} satisfies Meta<typeof MidwayList>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

/** Without a pace to go on (no timed reading yet) the hint drops the time estimate. */
export const NoEstimate: Story = {
  args: { items: [item(11, 20, 37, null), item(7, 12, 108, null)] },
}

export const Empty: Story = {
  args: { items: [] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing sits half read right now.')).toBeVisible()
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { IconBook2, IconClock, IconHourglassLow } from '@tabler/icons-react'
import { cover } from '../../../.storybook/mocks/api'
import { seriesById } from '../../../.storybook/mocks/shared'
import { RankList } from './RankList'

const row = (id: number, value: string) => ({ seriesId: id, title: seriesById(id).title, coverUrl: cover(id), value })

const meta = {
  component: RankList,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 420 }}>
        <Story />
      </div>
    ),
  ],
  args: {
    icon: IconBook2,
    title: 'Most read',
    emptyText: 'No chapters read in this period.',
    items: [row(1, '34 ch.'), row(6, '23 ch.'), row(5, '18 ch.'), row(9, '12 ch.'), row(3, '9 ch.')],
  },
} satisfies Meta<typeof RankList>

export default meta
type Story = StoryObj<typeof meta>

export const MostRead: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('link', { name: 'Lanterns Over Kasumi' })).toBeVisible()
    await expect(canvas.getByText('34 ch.')).toBeVisible()
  },
}

export const TimeSpent: Story = {
  args: {
    icon: IconClock,
    title: 'Where the time went',
    items: [row(1, '6h 12m'), row(6, '3h 40m'), row(5, '58m')],
  },
}

export const RemovedSeriesAndNoCover: Story = {
  args: {
    items: [
      { seriesId: null, title: 'Grave Robber of the Seventh Hall, the Complete Collected Chapters Edition', coverUrl: null, value: '14 ch.' },
      row(2, '3 ch.'),
    ],
  },
}

export const Empty: Story = {
  args: { icon: IconHourglassLow, title: 'Barely touched', items: [], emptyText: 'Everything you started, you kept reading.' },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('Everything you started, you kept reading.')).toBeVisible()
  },
}

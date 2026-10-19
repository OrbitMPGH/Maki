import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { librarySeries } from '../../../.storybook/mocks/home'
import { CoverCard } from './CoverCard'

const [lanterns, kitchen, ironPetal, , shiokaze, ashen, , bluefin, graveRobber, , neon] = librarySeries

const meta = {
  component: CoverCard,
  tags: ['ai-generated'],
  args: { series: lanterns, selectMode: false, selected: false, readTracking: true, onToggle: fn() },
  parameters: { layout: 'centered' },
  decorators: [
    (Story) => (
      <div style={{ width: 200 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof CoverCard>

export default meta
type Story = StoryObj<typeof meta>

export const PartlyRead: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('link')).toHaveAttribute('href', '/series/1')
    await expect(canvas.getByText('11 new')).toBeVisible()
  },
}

export const FullyRead: Story = {
  args: { series: shiokaze },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText(/new$/)).toBeNull()
    await expect(canvas.getByRole('img', { name: 'All downloaded chapters read' })).toBeInTheDocument()
  },
}

export const Completed: Story = {
  args: { series: kitchen },
}

export const DownloadingAndBehind: Story = {
  args: { series: ironPetal },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('60 of 88')).toBeVisible()
  },
}

export const NotMonitored: Story = {
  args: { series: neon },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('img', { name: 'Not monitored' })).toBeInTheDocument()
  },
}

export const Muted: Story = {
  args: { series: { ...ashen, notificationMode: 'Muted' } },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('img', { name: 'Notifications muted' })).toBeInTheDocument()
  },
}

export const ReadTrackingOff: Story = {
  args: { series: lanterns, readTracking: false },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText('11 new')).toBeNull()
  },
}

export const NeverTracked: Story = {
  args: { series: bluefin },
}

export const NoCover: Story = {
  args: { series: { ...graveRobber, coverUrl: null } },
}

export const LongTitle: Story = {
  args: { series: { ...graveRobber, displayTitle: 'Grave Robber of the Seventh Hall: The Complete Chronicle of Everything Below the Capital' } },
}

export const NoChaptersListed: Story = {
  args: {
    series: { ...graveRobber, totalChapters: null, wantedChapterCount: 0, knownChapterCount: 0, chapterFileCount: 0, readChapterCount: null },
  },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('No chapters listed')).toBeVisible()
  },
}

export const SelectMode: Story = {
  args: { selectMode: true },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('link'))
    await expect(args.onToggle).toHaveBeenCalledWith(1)
  },
}

export const Selected: Story = {
  args: { selectMode: true, selected: true },
}

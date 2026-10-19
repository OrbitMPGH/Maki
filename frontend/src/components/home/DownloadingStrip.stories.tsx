import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { downloadingItems } from '../../../.storybook/mocks/home'
import { DownloadingStrip } from './DownloadingStrip'

const meta = {
  component: DownloadingStrip,
  tags: ['ai-generated'],
  args: { items: downloadingItems.slice(0, 3) },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof DownloadingStrip>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('3 in the queue')).toBeVisible()
    await expect(canvas.getByRole('link', { name: 'View activity' })).toHaveAttribute('href', '/activity')
  },
}

export const SingleItem: Story = {
  args: { items: downloadingItems.slice(0, 1) },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('1 in the queue')).toBeVisible()
  },
}

export const MoreThanFiveRows: Story = {
  args: { items: downloadingItems },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('3 more in the queue')).toBeVisible()
  },
}

export const LongReleaseTitle: Story = {
  args: { items: [downloadingItems[7], downloadingItems[0]] },
}

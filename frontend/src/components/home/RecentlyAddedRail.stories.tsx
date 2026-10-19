import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { recentlyAdded } from '../../../.storybook/mocks/home'
import { RecentlyAddedRail } from './RecentlyAddedRail'

const meta = {
  component: RecentlyAddedRail,
  tags: ['ai-generated'],
  args: { items: recentlyAdded },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof RecentlyAddedRail>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    // A series with everything read has no Read button.
    const withRead = recentlyAdded.filter((i) => i.readChapterId != null).length
    await expect(canvas.getAllByRole('button', { name: 'Read next chapter' })).toHaveLength(withRead)
  },
}

export const SingleItem: Story = {
  args: { items: recentlyAdded.slice(0, 1) },
}

export const AllRead: Story = {
  args: { items: recentlyAdded.map((i) => ({ ...i, readChapterId: null })) },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('button', { name: 'Read next chapter' })).toBeNull()
  },
}

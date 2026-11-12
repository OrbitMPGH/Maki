import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import PageScrubber from './PageScrubber'

const meta = {
  component: PageScrubber,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: {
    count: 18,
    page: 5,
    shownTo: 5,
    rtl: false,
    onSeek: fn(),
    thumbs: [],
  },
  decorators: [
    (Story) => (
      <div className="reader-root" style={{ position: 'relative', height: 80, padding: '24px 16px' }}>
        <div style={{ display: 'flex' }}>
          <Story />
        </div>
      </div>
    ),
  ],
} satisfies Meta<typeof PageScrubber>

export default meta
type Story = StoryObj<typeof meta>

export const BookmarksAndNotes: Story = {
  args: { bookmarked: new Set([3, 9]), noted: new Set([9, 12]) },
  play: async ({ canvas }) => {
    const slider = await canvas.findByRole('slider', { name: 'Page' })
    const flagged = slider.querySelectorAll('.reader-scrub-segment[data-bookmarked]')
    await expect(flagged).toHaveLength(2)
    await expect(slider.querySelectorAll('.reader-scrub-segment[data-noted]')).toHaveLength(2)
    // A page with both carries both markers.
    await expect(slider.querySelectorAll('.reader-scrub-segment[data-bookmarked][data-noted]')).toHaveLength(1)
  },
}

export const NoBookmarks: Story = {
  play: async ({ canvas }) => {
    const slider = await canvas.findByRole('slider', { name: 'Page' })
    await expect(slider.querySelectorAll('.reader-scrub-segment[data-bookmarked]')).toHaveLength(0)
  },
}

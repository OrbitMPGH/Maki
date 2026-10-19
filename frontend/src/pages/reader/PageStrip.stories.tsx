import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { pageUrlsFor } from '../../../.storybook/mocks/entry/fixtures'
import PageStrip from './PageStrip'

const meta = {
  component: PageStrip,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    urls: pageUrlsFor(1132, 18, true),
    page: 5,
    bookmarks: new Set<number>([3, 9]),
    onSelect: fn(),
    rtl: true,
  },
  decorators: [
    (Story) => (
      <div className="reader-root">
        <div className="reader-strip-wrap" data-visible="true">
          <Story />
        </div>
      </div>
    ),
  ],
} satisfies Meta<typeof PageStrip>

export default meta
type Story = StoryObj<typeof meta>

export const RightToLeft: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(canvas.getByRole('button', { name: 'Go to page 6' })).toHaveAttribute('aria-current', 'true')
    await expect(canvas.getByRole('button', { name: 'Go to page 4 (bookmarked)' })).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Go to page 8' }))
    await expect(args.onSelect).toHaveBeenCalledWith(7)
  },
}

export const LeftToRight: Story = {
  args: { rtl: false, page: 0 },
}

export const LongChapter: Story = {
  args: { urls: pageUrlsFor(3040, 60, true), page: 41, bookmarks: new Set<number>() },
}

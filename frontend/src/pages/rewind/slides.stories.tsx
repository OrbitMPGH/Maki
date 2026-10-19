import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { activityFor } from '../../../.storybook/mocks/insights'
import { RewindIntro } from './RewindIntro'

// slides.tsx only exports `buildSlides`, so each story plays the real overlay and steps to the slide.
const stats = activityFor('2026-01-01', '2026-12-31', 'rich')

const meta = {
  component: RewindIntro,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: { stats, label: '2026', onClose: fn() },
} satisfies Meta<typeof RewindIntro>

export default meta
type Story = StoryObj<typeof meta>

function slide(index: number, text: string | RegExp): Story {
  return {
    play: async ({ canvasElement, userEvent }) => {
      const body = within(canvasElement.ownerDocument.body)
      await body.findByRole('dialog')
      for (let i = 0; i < index; i++) await userEvent.keyboard('{ArrowRight}')
      await waitFor(() => expect(body.getByText(text)).toBeVisible(), { timeout: 4000 })
    },
  }
}

export const Title: Story = slide(0, 'Maki Rewind')
export const ChaptersRead: Story = slide(1, 'You turned a lot of pages')
export const TimeInReader: Story = slide(2, 'Time spent in the reader')
export const BusiestMonth: Story = slide(3, 'Your busiest month')
export const MostReadSeries: Story = slide(4, 'Your most read series')
export const FavouriteGenres: Story = slide(5, 'You kept coming back to')
export const LibraryGrowth: Story = slide(6, 'Your library grew')
export const Finished: Story = slide(7, 'Seen through to the end')
export const Stalled: Story = slide(8, 'Maybe next year')
export const Summary: Story = slide(9, 'That was your 2026')

/** One finished series gets its own poster and title instead of a fan of covers. */
export const SingleFinish: Story = {
  args: { stats: { ...stats, finished: stats.finished.slice(0, 1) } },
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await body.findByRole('dialog')
    for (let i = 0; i < 7; i++) await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(body.getByText('The Ninth Kitchen')).toBeVisible(), { timeout: 4000 })
  },
}

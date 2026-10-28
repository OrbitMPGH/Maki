import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { activityFor } from '../../../.storybook/mocks/insights'
import { RewindIntro } from './RewindIntro'

// slides.tsx only exports `buildSlides`, so the stories play the real overlay and step through it.
const stats = activityFor('2026-01-01', '2026-12-31', 'rich')

const meta = {
  component: RewindIntro,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: { stats, label: '2026', onClose: fn() },
} satisfies Meta<typeof RewindIntro>

export default meta
type Story = StoryObj<typeof meta>

const SLIDES = [
  'Maki Rewind',
  'You turned a lot of pages',
  'Time spent in the reader',
  'Your busiest month',
  'Your most read series',
  'You kept coming back to',
  'Your library grew',
  'Seen through to the end',
  'Maybe next year',
  'That was your 2026',
]

/** Steps through every slide in order with the arrow key. */
export const AllSlides: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await body.findByRole('dialog')
    for (const [i, text] of SLIDES.entries()) {
      if (i > 0) await userEvent.keyboard('{ArrowRight}')
      await waitFor(() => expect(body.getByText(text)).toBeVisible())
    }
  },
}

/** One finished series gets its own poster and title instead of a fan of covers. */
export const SingleFinish: Story = {
  args: { stats: { ...stats, finished: stats.finished.slice(0, 1) } },
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await body.findByRole('dialog')
    for (let i = 0; i < 7; i++) await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(body.getByText('The Ninth Kitchen')).toBeVisible())
  },
}

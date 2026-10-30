import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { chapters, makeManifest } from '../../../.storybook/mocks/entry/fixtures'
import ChapterEnd from './ChapterEnd'
import { RatingCheckpointPrompt } from '../../components/series/RatingCheckpointPrompt'

const meta = {
  component: ChapterEnd,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    manifest: chapters[1132],
    readingCounted: true,
    rtl: true,
    onNext: fn(),
    onStay: fn(),
  },
  decorators: [
    (Story) => (
      <div className="reader-root">
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ChapterEnd>

export default meta
type Story = StoryObj<typeof meta>

export const NextChapterAvailable: Story = {
  play: async ({ args, canvas, userEvent }) => {
    // Focus lands on the heading so the page turn that opened this screen cannot press a button.
    await waitFor(() => expect(canvas.getByRole('heading', { name: 'Chapter 132' })).toHaveFocus())
    await expect(await canvas.findByText('132 of 142 read')).toBeVisible()
    await expect(await canvas.findByText('Up next')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: /Read Chapter 133/ }))
    await expect(args.onNext).toHaveBeenCalledOnce()
    await userEvent.click(await canvas.findByRole('button', { name: 'Stay here' }))
    await expect(args.onStay).toHaveBeenCalledOnce()
  },
}

export const CaughtUpRatingPrompt: Story = {
  args: {
    ratingPrompt: <RatingCheckpointPrompt checkpoint="caughtUp" soFar={7} onRate={fn()} onDismiss={fn()} />,
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("You're caught up. How is it so far?")).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Still a 7' })).toBeVisible()
  },
}

export const LeftToRightHint: Story = {
  args: { rtl: false },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/→ or Space opens it/)).toBeVisible()
  },
}

export const SkipsOneChapter: Story = {
  args: {
    manifest: makeManifest({
      ...chapters[1132],
      number: 19,
      nextChapterNumber: 21,
      nextChapterLabel: 'Chapter 21',
    }),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Chapter 20 isn't downloaded, so this skips it.")).toBeVisible()
  },
}

export const SkipsSeveralChapters: Story = {
  args: { manifest: chapters[6019] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Chapters 20 to 21 aren't downloaded, so this skips them.")).toBeVisible()
  },
}

export const ReadingNotCounted: Story = {
  args: { readingCounted: false },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('131 of 142 read')).toBeVisible()
  },
}

export const MoreChaptersWanted: Story = {
  args: { manifest: chapters[1142] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("You're caught up")).toBeVisible()
    await expect(await canvas.findByText('8 more chapters are wanted. They show up here once they download.')).toBeVisible()
    await expect(await canvas.findByRole('link', { name: 'Back to series' })).toBeVisible()
  },
}

export const EveryChapterRead: Story = {
  args: { manifest: chapters[2096] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("That's every chapter on disk.")).toBeVisible()
  },
}

export const EarlierChaptersStillUnread: Story = {
  args: {
    manifest: makeManifest({
      ...chapters[1142],
      seriesId: 3,
      seriesTitle: 'Iron Petal Academy',
      seriesChapterCount: 88,
      seriesReadCount: 40,
      seriesWantedCount: 88,
    }),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Last chapter on disk')).toBeVisible()
    await expect(await canvas.findByText('47 earlier chapters are still unread.')).toBeVisible()
  },
}

export const OneMoreWanted: Story = {
  args: { manifest: makeManifest({ ...chapters[1142], seriesWantedCount: 143 }) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('1 more chapter is wanted. It shows up here once it downloads.')).toBeVisible()
  },
}

export const NoCover: Story = {
  args: { manifest: makeManifest({ ...chapters[1132], seriesCoverUrl: null }) },
}

export const Mobile: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
}

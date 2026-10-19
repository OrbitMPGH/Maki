import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { chapters, makeManifest } from '../../../.storybook/mocks/entry/fixtures'
import ChapterEnd from './ChapterEnd'

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
    await expect(await canvas.findByRole('heading', { name: 'Chapter 132' })).toHaveFocus()
    await expect(canvas.getByText('132 of 142 read')).toBeVisible()
    await expect(canvas.getByText('Up next')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: /Read Chapter 133/ }))
    await expect(args.onNext).toHaveBeenCalledOnce()
    await userEvent.click(canvas.getByRole('button', { name: 'Stay here' }))
    await expect(args.onStay).toHaveBeenCalledOnce()
  },
}

export const LeftToRightHint: Story = {
  args: { rtl: false },
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/→ or Space opens it/)).toBeVisible()
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
    await expect(canvas.getByText("Chapter 20 isn't downloaded, so this skips it.")).toBeVisible()
  },
}

export const SkipsSeveralChapters: Story = {
  args: { manifest: chapters[6019] },
  play: async ({ canvas }) => {
    await expect(canvas.getByText("Chapters 20 to 21 aren't downloaded, so this skips them.")).toBeVisible()
  },
}

export const ReadingNotCounted: Story = {
  args: { readingCounted: false },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('131 of 142 read')).toBeVisible()
  },
}

export const MoreChaptersWanted: Story = {
  args: { manifest: chapters[1142] },
  play: async ({ canvas }) => {
    await expect(canvas.getByText("You're caught up")).toBeVisible()
    await expect(canvas.getByText('8 more chapters are wanted. They show up here once they download.')).toBeVisible()
    await expect(canvas.getByRole('link', { name: 'Back to series' })).toBeVisible()
  },
}

export const EveryChapterRead: Story = {
  args: { manifest: chapters[2096] },
  play: async ({ canvas }) => {
    await expect(canvas.getByText("That's every chapter on disk.")).toBeVisible()
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
    await expect(canvas.getByText('Last chapter on disk')).toBeVisible()
    await expect(canvas.getByText('47 earlier chapters are still unread.')).toBeVisible()
  },
}

export const OneMoreWanted: Story = {
  args: { manifest: makeManifest({ ...chapters[1142], seriesWantedCount: 143 }) },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('1 more chapter is wanted. It shows up here once it downloads.')).toBeVisible()
  },
}

export const NoCover: Story = {
  args: { manifest: makeManifest({ ...chapters[1132], seriesCoverUrl: null }) },
}

export const Mobile: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { chapters, makeManifest } from '../../../.storybook/mocks/entry/fixtures'
import { downloadingItem, queuedItems, queuePage } from '../../../.storybook/mocks/operations/queue'
import { finishedOutlook, lateOutlook } from '../../../.storybook/mocks/operations/upcoming'
import type { ReaderEnding } from '../../api/reader'
import ChapterEnd from './ChapterEnd'
import { RatingCheckpointPrompt } from '../../components/series/RatingCheckpointPrompt'

const ending = (over: Partial<ReaderEnding> = {}): ReaderEnding => ({
  nextChapterId: null,
  nextChapterLabel: null,
  nextChapterNumber: null,
  seriesChapterCount: 96,
  seriesReadCount: 95,
  seriesWantedCount: 96,
  completed: false,
  finished: false,
  rating: null,
  provisionalRating: null,
  rateHintDismissed: false,
  leftOutToComplete: null,
  ...over,
})

const withEnding = (body: ReaderEnding) => http.get(api('/reader/chapter/:id/ending'), () => HttpResponse.json(body))

/** The last chapter of a series the server has marked completed. */
const finalChapter = makeManifest({ ...chapters[2096], finishesSeries: true })

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
    onOpenNotes: fn(),
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

// The server only asks once no further chapter is expected, so the outlook here has nothing to say.
const keptChaptersRead = [
  withEnding(ending({ seriesChapterCount: 96, seriesReadCount: 96, seriesWantedCount: 96, completed: true, leftOutToComplete: 3 })),
  http.get(api('/series/:id/outlook'), () => HttpResponse.json(finishedOutlook)),
  http.put(api('/series/:id/reading-status'), () => HttpResponse.json({ readingStatus: 'Completed' })),
]

export const EveryKeptChapterReadAsksIfDone: Story = {
  args: { manifest: chapters[2096] },
  parameters: { msw: keptChaptersRead },
  play: async ({ canvas }) => {
    await expect(
      await canvas.findByText("You've read every chapter you kept. 3 chapters were left out. Done with this series?"),
    ).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Not yet' })).toBeVisible()
  },
}

export const MarkCompletedFromTheEndScreen: Story = {
  args: { manifest: chapters[2096] },
  parameters: { msw: keptChaptersRead },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Mark completed' }))
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'Mark completed' })).toBeNull())
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

export const SharedNotesOnTheChapter: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText(/The last line reads differently/)).toBeVisible()
    await expect(await canvas.findByText(/Best fight so far/)).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'See all 2 notes' }))
    await waitFor(() => expect(args.onOpenNotes).toHaveBeenCalledOnce())
  },
}

export const NoNotesInvitesTheFirst: Story = {
  args: { manifest: chapters[1142] },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Add a note' }))
    await waitFor(() => expect(args.onOpenNotes).toHaveBeenCalledOnce())
  },
}

export const FinishedAsksForTheFinalRating: Story = {
  args: { manifest: finalChapter },
  parameters: { msw: [withEnding(ending())] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('You finished the series')).toBeVisible()
    await expect(await canvas.findByText("You've finished this. How was it?")).toBeVisible()
    await expect(await canvas.findByText("That's the whole series.")).toBeVisible()
    await expect(canvas.queryByText(/About .* left in the series/)).toBeNull()
  },
}

export const FinishedKeepsTheSoFarScore: Story = {
  args: { manifest: finalChapter },
  parameters: {
    msw: [
      withEnding(ending({ provisionalRating: 8 })),
      http.post(api('/series/:id/rating/finalize'), () => HttpResponse.json({ rating: 8, provisionalRating: null })),
    ],
  },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('You had this at 8 so far. Keep it?')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Keep 8' }))
    await expect(await canvas.findByText('Rated 8/10')).toBeVisible()
  },
}

export const FinishedAlreadyRated: Story = {
  args: { manifest: finalChapter },
  parameters: { msw: [withEnding(ending({ rating: 9 }))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('You finished the series')).toBeVisible()
    await waitFor(() => expect(canvas.queryByText("You've finished this. How was it?")).toBeNull())
  },
}

export const FinishedBeforeTheWriteLands: Story = {
  args: { manifest: finalChapter, readingCounted: true },
  parameters: { msw: [withEnding(ending({ completed: true, finished: true, seriesReadCount: 96 }))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('96 of 96 read')).toBeVisible()
    await expect(await canvas.findByText("You've finished this. How was it?")).toBeVisible()
  },
}

export const FinishedIncognitoAsksNothing: Story = {
  args: { manifest: finalChapter, readingCounted: false },
  parameters: { msw: [withEnding(ending({ seriesReadCount: 96 }))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("That's every chapter on disk.")).toBeVisible()
    await expect(canvas.queryByText('You finished the series')).toBeNull()
  },
}

export const TimeLeftInTheSeries: Story = {
  args: { manifest: makeManifest({ ...chapters[1132], readingPace: { chapterSeconds: 480, style: 'paged', totalChapters: 150 } }) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('About 2h 24m left in the series')).toBeVisible()
  },
}

export const CaughtUpWithReleaseOutlook: Story = {
  args: { manifest: makeManifest({ ...chapters[1142], seriesWantedCount: 142 }) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("You're caught up")).toBeVisible()
    await expect(await canvas.findByText(/The next chapter usually comes in 3 to 5 days/)).toBeVisible()
    await expect(await canvas.findByText(/High confidence/)).toBeVisible()
  },
}

export const CaughtUpButLate: Story = {
  args: { manifest: makeManifest({ ...chapters[1142], seriesWantedCount: 142 }) },
  parameters: { msw: [http.get(api('/series/:id/outlook'), () => HttpResponse.json(lateOutlook))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The next chapter is later than usual')).toBeVisible()
  },
}

export const NextChapterFinishesDownloading: Story = {
  args: { manifest: chapters[1142] },
  parameters: {
    msw: [
      withEnding(
        ending({
          nextChapterId: 1143,
          nextChapterLabel: 'Chapter 143',
          nextChapterNumber: 143,
          seriesChapterCount: 143,
          seriesReadCount: 141,
          seriesWantedCount: 150,
        }),
      ),
    ],
  },
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('Up next')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: /Read Chapter 143/ }))
    await expect(args.onNext).toHaveBeenCalledWith(1143)
  },
}

export const NextChapterIsDownloading: Story = {
  args: { manifest: chapters[1142] },
  parameters: {
    msw: [http.get(api('/queue'), () => HttpResponse.json(queuePage([downloadingItem, queuedItems[0]])))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("You're caught up")).toBeVisible()
    await expect(await canvas.findByText(/Downloading Ch. 143, 64%./)).toBeVisible()
    await expect(await canvas.findByText(/1 more is queued\./)).toBeVisible()
    await expect(canvas.queryByText(/more chapters are wanted/)).toBeNull()
  },
}

export const NextChaptersAreQueued: Story = {
  args: { manifest: chapters[1142] },
  parameters: {
    msw: [http.get(api('/queue'), () => HttpResponse.json(queuePage([queuedItems[0], queuedItems[1]].map((q) => ({ ...q, seriesId: 1 })))))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('2 chapters are queued to download.')).toBeVisible()
  },
}

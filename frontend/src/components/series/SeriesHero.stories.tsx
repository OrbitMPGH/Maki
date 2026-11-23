import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { Button, Tabs } from '@mantine/core'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../../.storybook/mocks/api'
import { seriesDto, seriesMsw } from '../../../.storybook/mocks/series'
import { chaptersFor } from '../../../.storybook/mocks/series/fixtures'
import { SeriesHero, SeriesHeroSkeleton } from './SeriesHero'

const tabs = (
  <Tabs.List>
    <Tabs.Tab value="details">Details</Tabs.Tab>
    <Tabs.Tab value="chapters">Chapters</Tabs.Tab>
    <Tabs.Tab value="files">Files</Tabs.Tab>
  </Tabs.List>
)

const meta = {
  component: SeriesHero,
  tags: ['ai-generated'],
  args: {
    series: seriesDto(1),
    onRate: fn(),
    onFinalize: fn(),
    actions: <Button>Continue reading</Button>,
    tabs,
  },
  parameters: { layout: 'fullscreen', router: { initialEntries: ['/series/1'] }, msw: seriesMsw() },
  decorators: [
    (Story) => (
      <Tabs defaultValue="details" variant="unstyled" classNames={{ list: 'series-tabs', tab: 'series-tab' }}>
        <Story />
      </Tabs>
    ),
  ],
} satisfies Meta<typeof SeriesHero>

export default meta
type Story = StoryObj<typeof meta>

export const Ongoing: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 1, name: /Lanterns Over Kasumi/ })).toBeVisible()
    await expect(await canvas.findByRole('tab', { name: 'Chapters' })).toBeVisible()
  },
}

export const ReadingStatus: Story = {
  args: { series: seriesDto(1, { readingStatus: 'OnHold', readingStatusAuto: true }), onSetReadingStatus: fn() },
  play: async ({ canvas, canvasElement, args, userEvent }) => {
    const chip = await canvas.findByRole('button', { name: 'Reading status: On hold' })
    await expect(chip).toBeVisible()
    await userEvent.click(chip)
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('menuitem', { name: 'Dropped' }))
    await waitFor(() => expect(args.onSetReadingStatus).toHaveBeenCalledWith('Dropped'))
  },
}

export const Completed: Story = {
  args: { series: seriesDto(2) },
}

export const Rated: Story = {
  args: { series: seriesDto(3, { rating: 8 }) },
}

export const RatedSoFar: Story = {
  args: { series: seriesDto(3, { rating: null, provisionalRating: 7, provisionalAtChapter: 34, readChapterCount: 34 }) },
  play: async ({ canvas, args, userEvent }) => {
    await expect(await canvas.findByRole('button', { name: 'Your rating: Good 7 so far' })).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Make this my rating' }))
    await waitFor(() => expect(args.onFinalize).toHaveBeenCalledOnce())
  },
}

export const QuickRate: Story = {
  args: { series: seriesDto(3, { rating: 7 }) },
  play: async ({ canvas, args, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Rate from 1 to 10' }))
    const scores = await canvas.findByRole('group', { name: 'Rate from 1 to 10' })
    await userEvent.click(within(scores).getByRole('button', { name: 'Excellent 9' }))
    await waitFor(() => expect(args.onRate).toHaveBeenCalledWith(9))
  },
}

export const QuickRateByKeyboard: Story = {
  args: { series: seriesDto(3, { rating: 7 }) },
  play: async ({ canvas, args, userEvent }) => {
    ;(await canvas.findByRole('button', { name: 'Rate from 1 to 10' })).focus()
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Good 7' })).toHaveFocus())
    await userEvent.keyboard('{ArrowRight}{Enter}')
    await waitFor(() => expect(args.onRate).toHaveBeenCalledWith(8))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Rate from 1 to 10' })).toHaveFocus())
  },
}

/** Escape folds the buttons away and returns focus; reopening at once must not leave them stuck on the dashes. */
export const QuickRateEscapeAndReopen: Story = {
  args: { series: seriesDto(3, { rating: 7 }) },
  play: async ({ canvas, userEvent }) => {
    const dashes = await canvas.findByRole('button', { name: 'Rate from 1 to 10' })
    dashes.focus()
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Good 7' })).toHaveFocus())
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(dashes).toHaveFocus())
    await userEvent.keyboard('{ArrowLeft}')
    const seven = await canvas.findByRole('button', { name: 'Good 7' })
    await waitFor(() => expect(seven).toHaveFocus())
    await waitFor(() => expect(getComputedStyle(seven).transform).toBe('none'))
  },
}

/** A keyboard pick does not lock out the mouse: hovering the dashes still opens the buttons. */
export const QuickRateKeyboardThenHover: Story = {
  args: { series: seriesDto(3, { rating: 7 }) },
  play: async ({ canvas, args, userEvent }) => {
    const dashes = await canvas.findByRole('button', { name: 'Rate from 1 to 10' })
    dashes.focus()
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Good 7' })).toHaveFocus())
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(args.onRate).toHaveBeenCalledWith(7))
    await userEvent.hover(dashes)
    await expect(await canvas.findByRole('group', { name: 'Rate from 1 to 10' })).toBeVisible()
  },
}

/** A touch screen has no hover: a tap on the dashes opens the panel with the words. */
export const QuickRateTouchOpensPanel: Story = {
  args: { series: seriesDto(3, { rating: 7 }) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const dashes = await canvas.findByRole('button', { name: 'Rate from 1 to 10' })
    await userEvent.pointer({ keys: '[TouchA]', target: dashes })
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('button', { name: 'Masterpiece' })).toBeVisible()
    await expect(canvas.queryByRole('group', { name: 'Rate from 1 to 10' })).toBeNull()
  },
}

export const RatingHistory: Story = {
  args: { series: seriesDto(3, { rating: 9 }) },
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id/rating-history'), () =>
        HttpResponse.json([
          { rating: 7, final: false, atChapter: 12, origin: 'Checkpoint', createdAt: '2026-09-01T10:00:00Z' },
          { rating: 9, final: true, atChapter: 120, origin: 'Finish', createdAt: '2026-10-10T10:00:00Z' },
        ]),
      ),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Your rating over time' }))
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText('7/10 so far, at ch. 12')).toBeVisible()
    await expect(await body.findByText('9/10, your rating at ch. 120')).toBeVisible()
  },
}

export const LockedTitle: Story = {
  args: {
    series: seriesDto(1, { lockedFields: ['title', 'overview'] }),
  },
}

const longTitle =
  'Exiled From the Lantern Guild on My Very First Night: My Childhood Friends Left Me in the Fog, But Thanks to a Cursed Teapot I Brewed a LVL 999 Tea Ceremony and Now the Whole Kingdom Wants a Cup'

/** A light novel title at the length they really run to: it shrinks toward three lines. */
export const VeryLongTitle: Story = {
  args: {
    series: seriesDto(1, {
      title: longTitle,
      displayTitle: longTitle,
    }),
  },
  play: async ({ canvas }) => {
    const heading = await canvas.findByRole('heading', { level: 1, name: /Exiled From the Lantern Guild/ })
    await expect(heading).toHaveAttribute('data-length', 'long')
  },
}

export const WithAnimeAdaptation: Story = {
  args: { series: seriesDto(1, { hasAnime: true, animeName: 'Lanterns Over Kasumi (TV)' }) },
}

export const TitleLanguagePreferenceApplied: Story = {
  args: { series: seriesDto(1, { displayTitle: '霞の上の灯籠' }) },
}

export const SourcesFallShortOfTotal: Story = {
  args: { series: seriesDto(2, { totalChapters: 110 }) },
}

export const NothingWantedYet: Story = {
  args: {
    series: seriesDto(9, { wantedChapterCount: 0, chapterFileCount: 0, readChapterCount: null }),
  },
}

export const NoReadTracking: Story = {
  args: { series: seriesDto(3) },
  parameters: { msw: seriesMsw(http.get(api('/reader/used'), () => HttpResponse.json({ used: false }))) },
}

/** Every count on the page explained: specials, an unwanted chapter, the sources' reach, MangaBaka's total and where the reader is. */
export const ChapterCountsExplained: Story = {
  args: {
    series: seriesDto(1, {
      knownChapterCount: 42,
      wantedChapterCount: 41,
      chapterFileCount: 41,
      readChapterCount: 25,
      totalChapters: 45,
    }),
    readUpTo: 24,
    nextChapter: 'Vol.3 Ch.25',
  },
  parameters: {
    msw: seriesMsw(
      http.get(api('/chapter'), () =>
        HttpResponse.json(chaptersFor(1, { limit: 40, specials: [10.5, 20.5], unwanted: [40], trailingMissing: 0 })),
      ),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const button = await canvas.findByRole('button', { name: 'How these chapter counts add up' })
    await waitFor(() => expect(within(canvasElement).getByText('1 more listed, not wanted')).toBeVisible())
    await waitFor(() => expect(button).toHaveAttribute('aria-expanded', 'false'))
    button.focus()
    await userEvent.keyboard('{Enter}')
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'How these chapter counts add up' })
    await waitFor(() => expect(Array.from(dialog.querySelectorAll('p')).map((p) => p.textContent)).toEqual([
      '42 chapters are listed, the count on the Chapters tab and under Listed.',
      '2 of them have a decimal number or none, like extras or specials.',
      'Downloads counts the 41 chapters you want or already have. 1 more is listed but not wanted.',
      'Your sources go up to ch. 40.',
      'MangaBaka lists 45 chapters.',
      'You have finished 25 chapters, up to ch. 24. Next up is Vol.3 Ch.25.',
    ]))
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(button).toHaveAttribute('aria-expanded', 'false'))
  },
}

/** On disk counts the chapters wanted: 43 of the 45 listed, whatever MangaBaka's 56 says. */
export const ProgressAgainstWantedChapters: Story = {
  args: {
    series: seriesDto(1, {
      knownChapterCount: 45,
      wantedChapterCount: 43,
      chapterFileCount: 43,
      readChapterCount: 43,
      totalChapters: 56,
      progressTotal: 43,
      progressHave: 43,
      progressRead: 43,
      progressSkipped: 2,
      listedSpecials: 2,
    }),
    readUpTo: 43,
  },
  parameters: {
    msw: seriesMsw(
      http.get(api('/chapter'), () =>
        HttpResponse.json(chaptersFor(1, { limit: 40, specials: [10.5, 20.5], trailingMissing: 0 })),
      ),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await waitFor(() => expect(canvas.getAllByText('43 / 43 chapters')).toHaveLength(2))
    expect(canvas.queryByText('43 / 56 chapters')).toBeNull()
    const button = await canvas.findByRole('button', { name: 'How these chapter counts add up' })
    await userEvent.click(button)
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'How these chapter counts add up' })
    await waitFor(() =>
      expect(within(dialog).getByText('Downloads counts the 43 chapters you want or already have. 2 more are listed but not wanted.')).toBeVisible(),
    )
    await waitFor(() => expect(within(dialog).getByText('MangaBaka lists 56 chapters.')).toBeVisible())
  },
}

/** Smart mode keeps a few chapters on disk: Read counts those ten, On disk says 207 are wanted. */
export const SmartModeReadsWhatIsDownloaded: Story = {
  args: {
    series: seriesDto(1, {
      knownChapterCount: 207,
      wantedChapterCount: 207,
      chapterFileCount: 10,
      readChapterCount: 10,
      progressTotal: 207,
      progressHave: 10,
      progressRead: 10,
    }),
  },
  parameters: {
    msw: seriesMsw(http.get(api('/chapter'), () => HttpResponse.json(chaptersFor(1, { limit: 40, trailingMissing: 0 })))),
  },
  play: async ({ canvas }) => {
    await canvas.findByText('10 / 10 chapters')
    await canvas.findByText('10 / 207 chapters')
  },
}

export const NoCover: Story = {
  args: { series: seriesDto(4, { coverUrl: null }) },
}

export const Skeleton: StoryObj<typeof SeriesHeroSkeleton> = {
  render: () => <SeriesHeroSkeleton />,
  parameters: { msw: seriesMsw(http.get(api('/chapter'), () => neverResolve())) },
}

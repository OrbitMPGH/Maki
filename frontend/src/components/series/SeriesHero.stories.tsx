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

export const Completed: Story = {
  args: { series: seriesDto(2) },
}

export const Rated: Story = {
  args: { series: seriesDto(3, { rating: 8 }) },
}

export const RatedSoFar: Story = {
  args: { series: seriesDto(3, { rating: null, provisionalRating: 7, provisionalAtChapter: 34, readChapterCount: 34 }) },
  play: async ({ canvas, args, userEvent }) => {
    await expect(await canvas.findByText('so far, at ch. 34')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Make this my rating' }))
    await waitFor(() => expect(args.onFinalize).toHaveBeenCalledOnce())
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

export const LockedTitleAndManyAltTitles: Story = {
  args: {
    series: seriesDto(1, {
      lockedFields: ['title', 'overview'],
      altTitles: [
        { title: 'Lanterns Over Kasumi', language: 'en' },
        { title: 'Kasumi no Ue no Tourou', language: 'ja-ro' },
        { title: 'Faroles sobre Kasumi', language: 'es' },
        { title: 'Lanternes au-dessus de Kasumi', language: 'fr' },
        { title: 'Lanternas sobre Kasumi', language: 'pt-br' },
        { title: 'Laternen uber Kasumi', language: 'de' },
        { title: 'Lanterne su Kasumi', language: 'it' },
      ],
    }),
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
      '42 chapters are listed, the count on the Chapters tab and under Chapters known.',
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

export const NoCover: Story = {
  args: { series: seriesDto(4, { coverUrl: null }) },
}

export const Skeleton: StoryObj<typeof SeriesHeroSkeleton> = {
  render: () => <SeriesHeroSkeleton />,
  parameters: { msw: seriesMsw(http.get(api('/chapter'), () => neverResolve())) },
}

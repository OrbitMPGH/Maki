import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { Button, Tabs } from '@mantine/core'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../../.storybook/mocks/api'
import { seriesDto, seriesMsw } from '../../../.storybook/mocks/series'
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
    await expect(canvas.getByRole('tab', { name: 'Chapters' })).toBeVisible()
  },
}

export const Completed: Story = {
  args: { series: seriesDto(2) },
}

export const Rated: Story = {
  args: { series: seriesDto(3, { rating: 8 }) },
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

export const NoCover: Story = {
  args: { series: seriesDto(4, { coverUrl: null }) },
}

export const Skeleton: StoryObj<typeof SeriesHeroSkeleton> = {
  render: () => <SeriesHeroSkeleton />,
  parameters: { msw: seriesMsw(http.get(api('/chapter'), () => neverResolve())) },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { cover } from '../../../.storybook/mocks/api'
import { catalogueItem, popularPicks, recommendedPicks } from '../../../.storybook/mocks/home'
import { DiscoverRailRow, EngineCard, EngineRailRow, RecommendationCard, RecommendationRow } from './DiscoverRail'

const item = catalogueItem(1)
const owned = (id: string) => (id === popularPicks[popularPicks.length - 1].providerId ? 3 : null)

const meta = {
  component: RecommendationCard,
  tags: ['ai-generated'],
  args: { item, inLibrarySeriesId: null, onOpen: fn() },
  parameters: { layout: 'centered' },
  decorators: [
    (Story) => (
      <div style={{ width: 200 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof RecommendationCard>

export default meta
type Story = StoryObj<typeof meta>

export const NotInLibrary: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: `View and add ${item.title}` }))
    await waitFor(() => expect(args.onOpen).toHaveBeenCalledWith(item))
  },
}

export const InLibrary: Story = {
  args: { inLibrarySeriesId: 3 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: `View ${item.title}` })).toBeVisible()
  },
}

export const WithReason: Story = {
  args: { reasonOverride: undefined },
}

export const SequelReason: Story = {
  args: { item: catalogueItem(2, { relationKind: 'Sequel', relatedToTitle: 'Lanterns Over Kasumi' }) },
}

export const ReasonHidden: Story = {
  args: { reasonOverride: null },
}

export const NoRatingOrCover: Story = {
  args: { item: catalogueItem(3, { rating: null, thumbUrl: null, thumbUrlHiDpi: null, coverUrl: null, year: null, totalChapters: null }) },
}

export const LongTitle: Story = {
  args: { item: catalogueItem(4, { title: 'The Extremely Long Title of a Series That Keeps Going Past Every Reasonable Limit' }) },
}

export const ListRow: Story = {
  render: (args) => (
    <div style={{ width: 640 }}>
      <RecommendationRow item={args.item} inLibrarySeriesId={null} density="default" onOpen={args.onOpen} />
    </div>
  ),
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: new RegExp(`^${item.title}`) }))
    await waitFor(() => expect(args.onOpen).toHaveBeenCalledWith(item))
  },
}

export const ListRowOwnedCompact: Story = {
  render: (args) => (
    <div style={{ width: 640 }}>
      <RecommendationRow item={args.item} inLibrarySeriesId={3} density="compact" onOpen={args.onOpen} />
    </div>
  ),
}

export const ListRowComfortable: Story = {
  render: (args) => (
    <div style={{ width: 640 }}>
      <RecommendationRow item={args.item} inLibrarySeriesId={null} density="comfortable" onOpen={args.onOpen} />
    </div>
  ),
}

export const EngineCardWithReasons: Story = {
  render: (args) => (
    <EngineCard item={recommendedPicks[2]} inLibrarySeriesId={null} onOpen={args.onOpen} />
  ),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Fans of')).toBeVisible()
    await expect(await canvas.findByTitle(/Readers who finished .* also finished this/)).toBeVisible()
  },
}

export const EngineCardAuthorMatch: Story = {
  render: (args) => (
    <EngineCard item={recommendedPicks[1]} inLibrarySeriesId={7} onOpen={args.onOpen} />
  ),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('By an author you read')).toBeVisible()
  },
}

const longCoRead = 'The Shy Transfer Student Keeps Leaving Notes in My Locker and I Cannot Tell Who'
const longFeel = 'I Was Engaged to the Rival Heiress Who Hates Me More Than Anyone in Class'
const seedPicks = [
  { item: catalogueItem(31, { coRead: true, coReadWithTitle: longCoRead, becauseOfTitle: null, authorMatch: false }), seed: cover('seed-coread') },
  { item: catalogueItem(32, { becauseOfTitle: longFeel }), seed: cover('seed-feel') },
  { item: catalogueItem(33, { coRead: true, coReadWithTitle: 'Lanterns Over Kasumi', becauseOfTitle: null, authorMatch: false }), seed: cover('seed-short') },
  { item: catalogueItem(34, { relationKind: 'Sequel', relatedToTitle: 'Moonlit Ledger of the Northern Archive', becauseOfTitle: null }), seed: cover('seed-sequel') },
  { item: catalogueItem(35, { authorMatch: true, becauseOfTitle: null }), seed: null },
]

export const EngineCardSeedCoverCoRead: Story = {
  render: (args) => (
    <EngineCard item={seedPicks[0].item} inLibrarySeriesId={null} onOpen={args.onOpen} seedCoverUrl={seedPicks[0].seed} />
  ),
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByText('Fans of')).toBeVisible()
    await expect(await canvas.findByText(longCoRead)).toBeVisible()
    await waitFor(() => expect(canvasElement.querySelector('.engine-why-seed')).toBeVisible())
  },
}

export const EngineCardSeedWithoutCover: Story = {
  render: (args) => (
    <EngineCard item={seedPicks[1].item} inLibrarySeriesId={null} onOpen={args.onOpen} />
  ),
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByText('Feels like')).toBeVisible()
    await expect(canvasElement.querySelector('.engine-why-seed')).toBeNull()
  },
}

/** Every reason shape side by side, at the default rail width, with and without a seed cover. */
export const EngineCardReasonShapes: Story = {
  parameters: { layout: 'padded' },
  decorators: [(Story) => <div style={{ width: '100%' }}><Story /></div>],
  render: (args) => (
    <div style={{ display: 'grid', gap: 24 }}>
      {[true, false].map((withSeed) => (
        <section key={String(withSeed)}>
          <h3 style={{ margin: '0 0 8px' }}>{withSeed ? 'With seed cover' : 'Without seed cover'}</h3>
          <div style={{ display: 'flex', gap: 12 }}>
            {seedPicks.map(({ item: pick, seed }) => (
              <div key={pick.providerId} style={{ width: 226, flex: 'none' }}>
                <EngineCard item={pick} inLibrarySeriesId={null} onOpen={args.onOpen} seedCoverUrl={withSeed ? seed : null} />
              </div>
            ))}
          </div>
        </section>
      ))}
    </div>
  ),
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByRole('heading', { name: 'With seed cover' })).toBeVisible()
    await waitFor(() => expect(canvasElement.querySelectorAll('.engine-why-seed').length).toBe(4))
  },
}

export const RailOfCatalogueCards: Story = {
  parameters: { layout: 'padded' },
  decorators: [(Story) => <div style={{ width: '100%' }}><Story /></div>],
  render: (args) => (
    <DiscoverRailRow
      items={popularPicks}
      seriesIdFor={(i) => owned(i.providerId)}
      onOpen={args.onOpen}
    />
  ),
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByRole('button', { name: /View Iron Petal Academy/ })).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: /View and add Hollow Orchard Postman/ }))
    await expect(args.onOpen).toHaveBeenCalledOnce()
  },
}

export const RailWithReasons: Story = {
  parameters: { layout: 'padded' },
  decorators: [(Story) => <div style={{ width: '100%' }}><Story /></div>],
  render: (args) => <DiscoverRailRow items={recommendedPicks} seriesIdFor={() => null} onOpen={args.onOpen} showReason />,
}

export const EngineRail: Story = {
  parameters: { layout: 'padded' },
  decorators: [(Story) => <div style={{ width: '100%' }}><Story /></div>],
  render: (args) => <EngineRailRow items={recommendedPicks} seriesIdFor={() => null} onOpen={args.onOpen} />,
  play: async ({ canvasElement }) => {
    // The sequel and the co-read pick name series the mocked library owns, so both show its cover.
    await waitFor(() => expect(canvasElement.querySelectorAll('.engine-why-seed').length).toBe(2))
  },
}

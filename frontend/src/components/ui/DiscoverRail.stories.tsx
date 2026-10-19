import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
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
    await userEvent.click(canvas.getByRole('button', { name: `View and add ${item.title}` }))
    await expect(args.onOpen).toHaveBeenCalledWith(item)
  },
}

export const InLibrary: Story = {
  args: { inLibrarySeriesId: 3 },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: `View ${item.title}` })).toBeVisible()
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
    await userEvent.click(canvas.getByRole('button', { name: item.title }))
    await expect(args.onOpen).toHaveBeenCalledWith(item)
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
    await expect(canvas.getByText(/Readers who finished .* also finished this/)).toBeVisible()
  },
}

export const EngineCardAuthorMatch: Story = {
  render: (args) => (
    <EngineCard item={recommendedPicks[1]} inLibrarySeriesId={7} onOpen={args.onOpen} />
  ),
  play: async ({ canvas }) => {
    await expect(canvas.getByText('By an author you read')).toBeVisible()
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
    await expect(canvas.getByRole('button', { name: /View Iron Petal Academy/ })).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: /View and add Hollow Orchard Postman/ }))
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
}

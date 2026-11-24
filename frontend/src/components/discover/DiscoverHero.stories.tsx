import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import type { RecommendationItem } from '../../api/hooks'
import { discoverPage, recentRail } from '../../../.storybook/mocks/discover'
import { catalogue } from '../../../.storybook/mocks/shared'
import { IconIntersect, IconUsers } from '../../lib/icons'
import { engineReason } from '../ui/DiscoverRail'
import { DiscoverHero, type StartPick } from './DiscoverHero'

const source = { savedFrom: 'rail:hero' }
const pick = (item: RecommendationItem): StartPick => ({ item, reason: engineReason(item), source })
const lead = pick(recentRail.items[0])
const side: StartPick[] = [
  { item: recentRail.items[1], reason: { Glyph: IconUsers, text: 'Readers like you finished this' }, source },
  { item: recentRail.items[2], reason: { Glyph: IconIntersect, text: 'Another side of you: Found Family' }, source },
]

const meta = {
  component: DiscoverHero,
  tags: ['ai-generated'],
  args: {
    lead,
    side,
    onOpen: fn(),
    onRecommend: fn(),
    seriesIdFor: (_item: RecommendationItem): number | null => null,
  },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverHero>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByText('Feels like Lanterns Over Kasumi')).toBeVisible()
    await waitFor(() => expect(canvasElement.querySelector('.discover-hero .score-figure')).not.toBeNull())
    await userEvent.click(await canvas.findByRole('button', { name: 'More like this' }))
    await waitFor(() => expect(args.onRecommend).toHaveBeenCalledWith(args.lead.item))
  },
}

export const OpenASidePick: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: `View ${side[0].item.title}` }))
    await waitFor(() => expect(args.onOpen).toHaveBeenCalledWith(side[0].item, source))
  },
}

export const OwnedPick: Story = {
  args: {
    lead: pick({ ...recentRail.items[0], providerId: String(catalogue[0].mangaBakaId), title: catalogue[0].title }),
    seriesIdFor: (item) => (item.providerId === String(catalogue[0].mangaBakaId) ? 1 : null),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: /in library/ })).toBeVisible()
  },
}

export const LeadOnly: Story = {
  args: { side: [] },
}

export const BecauseOfRelation: Story = {
  args: { lead: pick(recentRail.items[4]) },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import type { RecommendationItem } from '../../api/hooks'
import { discoverPage, recentRail } from '../../../.storybook/mocks/discover'
import { catalogue } from '../../../.storybook/mocks/shared'
import { DiscoverHero } from './DiscoverHero'

const meta = {
  component: DiscoverHero,
  tags: ['ai-generated'],
  args: {
    items: recentRail.items,
    onOpen: fn(),
    onRecommend: fn(),
    seriesIdFor: (_item: RecommendationItem): number | null => null,
  },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverHero>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More like this' }))
    await waitFor(() => expect(args.onRecommend).toHaveBeenCalledWith(args.items[0]))
  },
}

export const PickAnother: Story = {
  play: async ({ canvas, userEvent }) => {
    const tabs = await canvas.findAllByRole('tab')
    await userEvent.click(tabs[1])
    await waitFor(() => expect(tabs[1]).toHaveAttribute('aria-selected', 'true'))
  },
}

export const OwnedPick: Story = {
  args: {
    items: [
      { ...recentRail.items[0], providerId: String(catalogue[0].mangaBakaId), title: catalogue[0].title },
      ...recentRail.items.slice(1),
    ],
    seriesIdFor: (item) => (item.providerId === String(catalogue[0].mangaBakaId) ? 1 : null),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: /in library/ })).toBeVisible()
  },
}

export const SinglePick: Story = {
  args: { items: recentRail.items.slice(0, 1) },
}

export const BecauseOfRelation: Story = {
  args: { items: [...recentRail.items.slice(4, 5), ...recentRail.items.slice(0, 4)] },
}

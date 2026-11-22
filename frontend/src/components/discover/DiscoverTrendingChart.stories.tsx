import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import type { RecommendationItem } from '../../api/hooks'
import { discoverPage, trendingRail } from '../../../.storybook/mocks/discover'
import { DiscoverTrendingChart } from './DiscoverTrendingChart'

const meta = {
  component: DiscoverTrendingChart,
  tags: ['ai-generated'],
  args: {
    items: trendingRail.items,
    onOpen: fn(),
    seriesIdFor: (_item: RecommendationItem): number | null => null,
  },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverTrendingChart>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('420 places this month')).toBeVisible()
    await userEvent.click(await canvas.findByText(args.items[1].title))
    await waitFor(() => expect(args.onOpen).toHaveBeenCalled())
  },
}

export const NoClimbs: Story = {
  args: { items: trendingRail.items.map((item) => ({ ...item, climb: null })) },
}

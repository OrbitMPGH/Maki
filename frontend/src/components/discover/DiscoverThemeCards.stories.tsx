import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import type { RecommendationItem } from '../../api/hooks'
import { discoverPage, sideInterestRails } from '../../../.storybook/mocks/discover'
import { DiscoverThemeCards } from './DiscoverThemeCards'

const meta = {
  component: DiscoverThemeCards,
  tags: ['ai-generated'],
  args: {
    rails: sideInterestRails,
    onOpen: fn(),
    onShowMore: fn(),
    seriesIdFor: (_item: RecommendationItem): number | null => null,
  },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverThemeCards>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    const seeAll = await canvas.findAllByRole('button', { name: /^See all in/ })
    await userEvent.click(seeAll[0])
    await waitFor(() => expect(args.onShowMore).toHaveBeenCalledWith(args.rails[0]))
  },
}

export const OneTheme: Story = {
  args: { rails: sideInterestRails.slice(0, 1) },
}

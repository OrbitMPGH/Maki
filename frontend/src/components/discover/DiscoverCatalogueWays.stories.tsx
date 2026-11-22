import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import type { RecommendationItem } from '../../api/hooks'
import { discoverPage } from '../../../.storybook/mocks/discover'
import { DiscoverCatalogueWays } from './DiscoverCatalogueWays'

const meta = {
  component: DiscoverCatalogueWays,
  tags: ['ai-generated'],
  args: {
    cols: { base: 2, sm: 4, lg: 6 },
    onOpen: fn(),
    seriesIdFor: (_item: RecommendationItem): number | null => null,
    children: <p>The catalogue feeds</p>,
  },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverCatalogueWays>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The catalogue feeds')).toBeVisible()
  },
}

export const ChooseAWay: Story = {
  play: async ({ canvas, userEvent }) => {
    const door = await canvas.findByRole('button', { name: /Short and finished/ })
    await userEvent.click(door)
    await waitFor(() => expect(door).toHaveAttribute('aria-pressed', 'true'))
    await expect(await canvas.findByRole('button', { name: /Show all/ })).toBeVisible()
    await waitFor(() => expect(canvas.queryByText('The catalogue feeds')).toBeNull())
  },
}

export const ChooseAGenre: Story = {
  play: async ({ canvas, userEvent }) => {
    const chip = await canvas.findByRole('button', { name: 'Mystery' })
    await userEvent.click(chip)
    await waitFor(() => expect(chip).toHaveAttribute('aria-pressed', 'true'))
  },
}

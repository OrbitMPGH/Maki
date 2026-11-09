import type { Meta, StoryObj } from '@storybook/react-vite'
import { Group } from '@mantine/core'
import { expect } from 'storybook/test'
import { ContentRatingChip } from './ContentRatingChip'

const meta = {
  component: ContentRatingChip,
  tags: ['ai-generated'],
  args: { rating: 'safe' },
} satisfies Meta<typeof ContentRatingChip>

export default meta
type Story = StoryObj<typeof meta>

/** Each rating is outlined in its own colour; Safe is neutral and the rest rise in heat. */
export const EveryRating: Story = {
  render: () => (
    <Group gap={8}>
      {['safe', 'suggestive', 'erotica', 'pornographic'].map((rating) => (
        <ContentRatingChip key={rating} rating={rating} />
      ))}
    </Group>
  ),
  play: async ({ canvas }) => {
    const colours = new Set<string>()
    for (const [label, rating] of [['Safe', 'safe'], ['Suggestive', 'suggestive'], ['Erotica', 'erotica'], ['Pornographic', 'pornographic']]) {
      const chip = await canvas.findByText(label)
      await expect(chip).toHaveAttribute('data-content-rating', rating)
      colours.add(getComputedStyle(chip).color)
    }
    await expect(colours.size).toBe(4)
  },
}

export const Unknown: Story = {
  args: { rating: null },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('.series-hero-status')).toBeNull()
  },
}

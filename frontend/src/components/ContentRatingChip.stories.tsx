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

/** Every rating is the same neutral outline; only the icon and the word differ. */
export const EveryRating: Story = {
  render: () => (
    <Group gap={8}>
      {['safe', 'suggestive', 'erotica', 'pornographic'].map((rating) => (
        <ContentRatingChip key={rating} rating={rating} />
      ))}
    </Group>
  ),
  play: async ({ canvas }) => {
    for (const label of ['Safe', 'Suggestive', 'Erotica', 'Pornographic']) {
      const chip = await canvas.findByText(label)
      await expect(chip).toHaveAttribute('data-quiet')
      await expect(chip.getAttribute('style')).toBeNull()
    }
  },
}

export const Unknown: Story = {
  args: { rating: null },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('.series-hero-status')).toBeNull()
  },
}

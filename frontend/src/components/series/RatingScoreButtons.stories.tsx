import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { RatingScoreButtons } from './RatingScoreButtons'

const meta = {
  component: RatingScoreButtons,
  tags: ['ai-generated'],
  args: { label: 'Rate this series', onPick: fn() },
} satisfies Meta<typeof RatingScoreButtons>

export default meta
type Story = StoryObj<typeof meta>

export const Unrated: Story = {
  play: async ({ args, canvas, userEvent }) => {
    const nine = await canvas.findByRole('button', { name: 'Excellent 9' })
    await userEvent.hover(nine)
    await expect(await canvas.findByText('Excellent 9', { selector: '.rating-scores-caption' })).toBeVisible()
    await userEvent.click(nine)
    await waitFor(() => expect(args.onPick).toHaveBeenCalledWith(9))
  },
}

/** A "so far" 6.5 fills 1 to 6 in grey and half of 7. */
export const SoFarDecimal: Story = {
  args: { soFar: 6.5 },
  play: async ({ canvas }) => {
    const seven = await canvas.findByRole('button', { name: 'Good 7' })
    await waitFor(() => expect(seven.style.getPropertyValue('--fill')).toBe('50%'))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Decent 6' }).style.getPropertyValue('--fill')).toBe('100%'))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Very good 8' }).style.getPropertyValue('--fill')).toBe('0%'))
  },
}

export const Rated: Story = {
  args: { value: 8 },
  play: async ({ canvas }) => {
    const eight = await canvas.findByRole('button', { name: 'Very good 8' })
    await waitFor(() => expect(eight).toHaveAttribute('aria-pressed', 'true'))
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { StatTile } from './StatTile'

const meta = {
  component: StatTile,
  tags: ['ai-generated'],
  args: { label: 'Chapters read', value: '1,284' },
} satisfies Meta<typeof StatTile>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const WithDelta: Story = {
  args: { delta: 0.12, deltaLabel: 'vs previous 30 days' },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('+12%')).toHaveAttribute('title', 'vs previous 30 days')
  },
}

export const InvertedDelta: Story = {
  args: { label: 'Series dropped', value: 4, delta: 0.5, invertDelta: true },
}

export const ZeroBaseline: Story = {
  args: { delta: null },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('-')).toBeVisible()
  },
}

export const WithHint: Story = {
  args: { label: 'Library', value: 98, hint: '12 ongoing' },
}

export const Loading: Story = {
  args: { loading: true },
}

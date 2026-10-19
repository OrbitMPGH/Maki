import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { RailSkeleton } from './RailSkeleton'

const meta = {
  component: RailSkeleton,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof RailSkeleton>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelectorAll('.discover-rail-item')).toHaveLength(12)
  },
}

export const WithTitle: Story = {
  args: { title: true },
}

export const Engine: Story = {
  args: { engine: true, count: 8, title: true, titleWidth: 240 },
}

export const FewCards: Story = {
  args: { count: 3 },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelectorAll('.discover-rail-item')).toHaveLength(3)
  },
}

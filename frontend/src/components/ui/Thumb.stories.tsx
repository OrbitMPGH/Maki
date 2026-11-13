import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Thumb } from './Thumb'

const meta = {
  component: Thumb,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: { src: '/api/v1/mediacover/1/cover.jpg', alt: 'Lanterns Over Kasumi' },
} satisfies Meta<typeof Thumb>

export default meta
type Story = StoryObj<typeof meta>

export const Medium: Story = {
  args: { size: 'md' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByAltText('Lanterns Over Kasumi')).toBeVisible()
  },
}

export const Sizes: Story = {
  render: (args) => (
    <div style={{ display: 'flex', gap: 16, alignItems: 'flex-end' }}>
      <Thumb {...args} size="sm" />
      <Thumb {...args} size="md" />
      <Thumb {...args} size="lg" />
    </div>
  ),
}

/** No cover: the brand tile, the same one a broken URL falls back to. */
export const NoCover: Story = {
  render: () => (
    <div style={{ display: 'flex', gap: 16, alignItems: 'flex-end' }}>
      <Thumb size="sm" />
      <Thumb size="md" />
      <Thumb size="lg" />
    </div>
  ),
}

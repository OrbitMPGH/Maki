import type { Meta, StoryObj } from '@storybook/react-vite'
import { cover } from '../../../.storybook/mocks/api'
import { HeroBackdrop } from './HeroBackdrop'

const meta = {
  component: HeroBackdrop,
  tags: ['ai-generated'],
  args: { coverUrl: cover(1) },
  decorators: [
    (Story) => (
      <div style={{ position: 'relative', height: 280, overflow: 'hidden' }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof HeroBackdrop>

export default meta
type Story = StoryObj<typeof meta>

export const WithCover: Story = {}

export const DifferentCover: Story = {
  args: { coverUrl: cover(3) },
}

export const NoCover: Story = {
  args: { coverUrl: null },
}

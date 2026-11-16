import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { ScoreInline, YourScoreInline } from './ScoreInline'

const meta = {
  component: ScoreInline,
  tags: ['ai-generated'],
  args: { score: 80 },
} satisfies Meta<typeof ScoreInline>

export default meta
type Story = StoryObj<typeof meta>

export const Community: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('8.0')).toBeInTheDocument()
  },
}

export const Medium: Story = { args: { score: 78, size: 'md' } }

export const Yours: Story = {
  render: () => <YourScoreInline rating={9} />,
  play: async ({ canvas }) => {
    await expect(canvas.getByText('9')).toBeInTheDocument()
  },
}

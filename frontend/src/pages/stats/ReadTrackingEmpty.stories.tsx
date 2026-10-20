import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { ReadTrackingEmpty } from './ReadTrackingEmpty'

const meta = {
  component: ReadTrackingEmpty,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof ReadTrackingEmpty>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No reading tracked yet')).toBeVisible()
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { SecureContextNote } from './SecureContextNote'

const meta = {
  component: SecureContextNote,
  tags: ['ai-generated'],
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 560 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof SecureContextNote>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('link', { name: 'Reverse proxy setup' })).toHaveAttribute('target', '_blank'))
  },
}

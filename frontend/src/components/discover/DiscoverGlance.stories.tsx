import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { detailFor } from '../../../.storybook/mocks/discover'
import { DiscoverGlance } from './DiscoverGlance'

const meta = {
  component: DiscoverGlance,
  tags: ['ai-generated'],
  args: { detail: detailFor('30000'), onNavigate: fn() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 320 }}>
        <Story />
      </div>
    ),
  ],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof DiscoverGlance>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('link', { name: 'Mio Tachibana' }))
    await expect(args.onNavigate).toHaveBeenCalled()
  },
}

export const Completed: Story = {
  args: { detail: detailFor('30006') },
}

export const ArtistAndStudios: Story = {
  args: { detail: detailFor('30002') },
}

export const SparseRecord: Story = {
  args: { detail: detailFor('30014') },
}

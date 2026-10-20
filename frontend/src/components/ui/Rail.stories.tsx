import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { IconBook } from '@tabler/icons-react'
import { Rail } from './Rail'
import { SectionHeader } from './SectionHeader'

function swatches(count: number) {
  return Array.from({ length: count }, (_, i) => (
    <div key={i} className="discover-rail-item">
      <div
        style={{
          aspectRatio: '2 / 3',
          borderRadius: 12,
          display: 'grid',
          placeItems: 'center',
          color: '#fff',
          fontWeight: 700,
          background: `hsl(${(i * 37) % 360}, 45%, 38%)`,
        }}
      >
        {i + 1}
      </div>
    </div>
  ))
}

const meta = {
  component: Rail,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: { children: swatches(24) },
  // The arrows land in the slot of the SectionHeader just above the rail.
  decorators: [
    (Story) => (
      <div>
        <SectionHeader icon={IconBook} title="Continue reading" />
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof Rail>

export default meta
type Story = StoryObj<typeof meta>

export const Overflowing: Story = {
  play: async ({ canvas, userEvent }) => {
    const forward = await canvas.findByRole('button', { name: 'Scroll forward' })
    await waitFor(() => expect(forward).toBeEnabled())
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Scroll back' })).toBeDisabled())
    await userEvent.click(forward)
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Scroll back' })).toBeEnabled())
  },
}

export const FitsWithoutScrolling: Story = {
  args: { children: swatches(3) },
  play: async ({ canvas, canvasElement }) => {
    await waitFor(() => expect(canvasElement.querySelectorAll('.discover-rail-item')).toHaveLength(3))
    await expect(canvas.queryByRole('button', { name: 'Scroll forward' })).toBeNull()
  },
}

export const Engine: Story = {
  args: { engine: true },
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(canvasElement.querySelector('.discover-rail')).toHaveAttribute('data-engine', 'true'))
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { IconBook } from '../../lib/icons'
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

/** Offscreen, the cards are skipped (content-visibility: auto); back on screen the end must still fade. */
export const KeepsFadeAfterScrollingAway: Story = {
  decorators: [
    (Story) => (
      <>
        <Story />
        <div style={{ height: '400vh' }} />
      </>
    ),
  ],
  play: async ({ canvasElement }) => {
    const rail = canvasElement.querySelector<HTMLElement>('.discover-rail')!
    await waitFor(() => expect(rail).toHaveAttribute('data-fade-end'))
    const page = document.scrollingElement!
    for (const top of [page.scrollHeight, 0, page.scrollHeight, 0]) {
      page.scrollTop = top
      await new Promise((resolve) => setTimeout(resolve, 250))
    }
    await waitFor(() => expect(rail).toHaveAttribute('data-fade-end'))
    await expect(rail.scrollWidth).toBeGreaterThan(rail.clientWidth)
  },
}

export const Engine: Story = {
  args: { engine: true },
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(canvasElement.querySelector('.discover-rail')).toHaveAttribute('data-engine', 'true'))
  },
}

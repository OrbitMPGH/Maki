import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { FigureStrip } from './FigureStrip'

const meta = {
  component: FigureStrip,
  tags: ['ai-generated'],
  args: {
    figures: [
      { label: 'Queued', value: 12 },
      { label: 'Downloading', value: 3, tone: 'info' },
      { label: 'Completed', value: 1480, tone: 'ok' },
      { label: 'Failed', value: 0, tone: 'danger' },
    ],
  },
} satisfies Meta<typeof FigureStrip>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('1,480')).toBeVisible()
    // A zero is dimmed rather than coloured: an empty "Failed" is good news.
    await waitFor(() => expect(canvas.getByText('Failed').closest('.figure-strip-item')).toHaveAttribute('data-zero', 'true'))
    await waitFor(() => expect(canvas.getByText('Downloading').closest('.figure-strip-item')).not.toHaveAttribute('data-zero'))
  },
}

export const Flush: Story = {
  args: { flush: true },
}

/** Stats: the change against the previous window as a chip, left off a zero. */
export const WithDelta: Story = {
  args: {
    deltaLabel: 'vs previous 30 days',
    figures: [
      { label: 'Chapters', value: 1284, delta: 0.12 },
      { label: 'Pages', value: 0, delta: -1 },
      { label: 'Time read', value: '14h 20m', delta: -0.08 },
    ],
  },
  play: async ({ canvas, canvasElement }) => {
    await waitFor(() => expect(canvas.getByText('+12%')).toHaveAttribute('title', 'vs previous 30 days'))
    await waitFor(() => expect(canvasElement.querySelectorAll('.figure-strip-delta')).toHaveLength(2))
  },
}

/** Taste: values formatted by the caller, a dash for nothing measured, and a hint line. */
export const FormattedWithHints: Story = {
  args: {
    figures: [
      { label: 'You finish', value: '62%', hint: '31 of 50 series' },
      { label: 'Typical chapter', value: '-', zero: true },
      { label: 'Biggest day', value: '24 chapters' },
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('31 of 50 series')).toBeVisible()
    await waitFor(() =>
      expect(canvas.getByText('Typical chapter').closest('.figure-strip-item')).toHaveAttribute('data-zero', 'true'),
    )
  },
}

export const Loading: Story = {
  args: { loading: true },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Completed')).toBeVisible()
    await expect(canvas.queryByText('1,480')).not.toBeInTheDocument()
  },
}

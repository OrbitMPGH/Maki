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

export const Loading: Story = {
  args: { loading: true },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Completed')).toBeVisible()
    await expect(canvas.queryByText('1,480')).not.toBeInTheDocument()
  },
}

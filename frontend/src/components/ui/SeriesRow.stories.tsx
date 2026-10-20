import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { librarySeries } from '../../../.storybook/mocks/library'
import { SeriesRow } from './SeriesRow'

const byId = (id: number) => librarySeries.find((s) => s.id === id) ?? librarySeries[0]

const meta = {
  component: SeriesRow,
  tags: ['ai-generated'],
  args: {
    series: byId(1),
    selectMode: false,
    selected: false,
    readTracking: true,
    density: 'default',
    onToggle: fn(),
  },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof SeriesRow>

export default meta
type Story = StoryObj<typeof meta>

export const BehindAndQueued: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('link')).toHaveAttribute('href', '/series/1'))
    await expect(await canvas.findByText('140/142')).toBeVisible()
    await expect(await canvas.findByText('Queued 2')).toBeVisible()
  },
}

export const Completed: Story = {
  args: { series: byId(10) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('48/48')).toBeVisible()
    await expect(await canvas.findByText('Read')).toBeVisible()
  },
}

export const Downloading: Story = {
  args: { series: byId(3) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Downloading 3')).toBeVisible()
  },
}

export const MutedAndUnmonitored: Story = {
  args: { series: { ...byId(6), monitored: false } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Notifications muted' })).toBeVisible()
    await expect(await canvas.findByRole('img', { name: 'Not monitored' })).toBeVisible()
  },
}

export const NeverRead: Story = {
  args: { series: byId(8) },
}

export const NoReadTracking: Story = {
  args: { readTracking: false },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('.cover-ring')).toBeNull()
  },
}

export const ImportWithoutMetadata: Story = {
  args: { series: byId(13) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('9/9')).toBeVisible()
  },
}

export const LongTitle: Story = {
  args: { series: byId(14) },
}

export const Compact: Story = {
  args: { density: 'compact' },
}

export const Comfortable: Story = {
  args: { density: 'comfortable' },
}

export const SelectMode: Story = {
  args: { selectMode: true },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('link'))
    await waitFor(() => expect(args.onToggle).toHaveBeenCalledWith(1))
  },
}

export const Selected: Story = {
  args: { selectMode: true, selected: true },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { SeriesCell } from './SeriesCell'

const meta = {
  component: SeriesCell,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: {
    seriesId: 1,
    title: 'Lanterns Over Kasumi',
    coverUrl: '/api/v1/mediacover/1/cover.jpg',
    detail: 'Ch.143',
  },
} satisfies Meta<typeof SeriesCell>

export default meta
type Story = StoryObj<typeof meta>

export const Linked: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: 'Lanterns Over Kasumi' })).toHaveAttribute('href', '/series/1')
  },
}

export const Small: Story = {
  args: { small: true },
}

export const TitleOnly: Story = {
  args: { detail: undefined },
}

/** A tracker's title that no library series matched: plain text, an empty cover slot. */
export const NotInLibrary: Story = {
  args: { seriesId: null, coverUrl: null, title: 'Under the Willow Gate', detail: undefined },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Under the Willow Gate')).toBeVisible()
    await expect(canvas.queryByRole('link')).toBeNull()
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { Group } from '@mantine/core'
import { cover } from '../../../.storybook/mocks/api'
import { SeriesLink, SeriesThumb } from './SeriesLink'

const meta = {
  component: SeriesLink,
  tags: ['ai-generated'],
  args: { id: 1, title: 'Lanterns Over Kasumi' },
} satisfies Meta<typeof SeriesLink>

export default meta
type Story = StoryObj<typeof meta>

export const Linked: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('link', { name: 'Lanterns Over Kasumi' })).toHaveAttribute('href', '/series/1'))
  },
}

/** A series that has since been removed has no id, so it is plain text. */
export const Removed: Story = {
  args: { id: null, title: 'Under the Willow Gate' },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('link')).toBeNull()
    await expect(await canvas.findByText('Under the Willow Gate')).toBeVisible()
  },
}

export const OpensInDiscover: Story = {
  args: { id: null, title: 'Under the Willow Gate', onOpen: fn() },
  play: async ({ canvas, userEvent, args }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Open Under the Willow Gate in Discover' }))
    await expect(args.onOpen).toHaveBeenCalledOnce()
  },
}

export const Thumbs: Story = {
  render: () => (
    <Group>
      <SeriesThumb url={cover(1)} alt="Lanterns Over Kasumi" />
      <SeriesThumb url={cover(2)} alt="The Ninth Kitchen" />
      <SeriesThumb url={null} alt="No cover" />
      <SeriesThumb url={cover(3)} alt="Iron Petal Academy" large />
      <SeriesThumb url={null} alt="No cover" large />
    </Group>
  ),
}

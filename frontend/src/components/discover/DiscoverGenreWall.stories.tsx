import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { genreRails } from '../../../.storybook/mocks/discover'
import { DiscoverGenreWall } from './DiscoverGenreWall'

const meta = {
  component: DiscoverGenreWall,
  tags: ['ai-generated'],
  args: { rails: genreRails, onOpen: fn() },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof DiscoverGenreWall>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Fantasy/ }))
    await expect(args.onOpen).toHaveBeenCalledWith(expect.objectContaining({ genre: 'Fantasy' }))
  },
}

export const FewGenres: Story = {
  args: { rails: genreRails.slice(0, 3) },
}

export const NoCovers: Story = {
  args: { rails: genreRails.slice(0, 4).map((r) => ({ ...r, items: r.items.map((i) => ({ ...i, thumbUrl: null })) })) },
}

export const Empty: Story = {
  args: { rails: [] },
}

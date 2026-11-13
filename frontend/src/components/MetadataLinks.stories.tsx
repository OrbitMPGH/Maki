import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { links } from '../../.storybook/mocks/series'
import { catalogue } from '../../.storybook/mocks/shared'
import { MetadataLinks } from './MetadataLinks'

const meta = {
  component: MetadataLinks,
  tags: ['ai-generated'],
  args: { links: links(catalogue[0]) },
} satisfies Meta<typeof MetadataLinks>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByRole('link')).toHaveLength(3))
    await expect(await canvas.findByRole('link', { name: /open on anilist/i })).toBeVisible()
  },
}

export const Empty: Story = {
  args: { links: [] },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.queryAllByRole('link')).toHaveLength(0))
  },
}

export const WithScores: Story = {
  args: {
    links: [
      { site: 'mangabaka', url: 'https://mangabaka.org/1', score: '8.4' },
      { site: 'anilist', url: 'https://anilist.co/manga/1', score: 86, votes: 41200 },
      { site: 'myanimelist', url: 'https://myanimelist.net/manga/1', score: '8.62' },
      { site: 'mangadex', url: 'https://mangadex.org/title/1' },
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('8.62')).toBeVisible()
    await expect(canvas.getAllByRole('link')).toHaveLength(4)
  },
}

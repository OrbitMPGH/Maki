import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
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
    await expect(canvas.getAllByRole('link')).toHaveLength(3)
    await expect(canvas.getByRole('link', { name: /open on anilist/i })).toBeVisible()
  },
}

export const Compact: Story = {
  args: { compact: true },
}

export const Empty: Story = {
  args: { links: [] },
  play: async ({ canvas }) => {
    await expect(canvas.queryAllByRole('link')).toHaveLength(0)
  },
}

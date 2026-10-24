import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { detailFor } from '../../../.storybook/mocks/discover'
import { DiscoverTags } from './DiscoverTags'

const meta = {
  component: DiscoverTags,
  tags: ['ai-generated'],
  args: { tags: detailFor('30000').tags },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 640 }}>
        <Story />
      </div>
    ),
  ],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof DiscoverTags>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Travel')).toBeVisible()
  },
}

const crowd = Array.from({ length: 26 }, (_, i) => ({
  name: `Incidental theme ${i + 1}`,
  weight: 'incidental',
  description: null,
  isSpoiler: i % 9 === 0,
}))

export const ManyTags: Story = {
  args: { tags: [...detailFor('30000').tags, ...crowd] },
}

export const OnlySpoilers: Story = {
  args: {
    tags: [
      { name: 'Unreliable Narrator', weight: 'core', description: 'The viewpoint character gets key facts wrong.', isSpoiler: true },
      { name: 'Betrayal', weight: 'defining', description: null, isSpoiler: true },
    ],
  },
}

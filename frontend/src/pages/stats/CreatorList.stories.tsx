import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { Panel } from '../../components/ui/Panel'
import { CreatorList } from './CreatorList'

const meta = {
  component: CreatorList,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md" style={{ maxWidth: 380 }}>
        <Story />
      </Panel>
    ),
  ],
  args: {
    creators: [
      { name: 'Aoi Mizuhara', story: true, art: true, seriesRead: 2, chaptersRead: 179 },
      { name: 'Mei Okabe', story: false, art: true, seriesRead: 1, chaptersRead: 96 },
      { name: 'Haru Takeda', story: true, art: false, seriesRead: 1, chaptersRead: 96 },
      { name: 'Studio Lark', story: false, art: true, seriesRead: 1, chaptersRead: 150 },
    ],
  },
} satisfies Meta<typeof CreatorList>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('link', { name: 'Aoi Mizuhara' })).toHaveAttribute('href', '/creator/Aoi%20Mizuhara'))
    await expect(await canvas.findByText('Story · Art')).toBeVisible()
  },
}

export const CappedAtThree: Story = {
  args: { max: 3 },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText('Studio Lark')).toBeNull()
  },
}

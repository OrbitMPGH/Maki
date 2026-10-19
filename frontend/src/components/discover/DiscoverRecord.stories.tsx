import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Creators, DetailRecord } from './DiscoverRecord'

const meta = {
  component: DetailRecord,
  tags: ['ai-generated'],
  args: { label: 'Published', children: '2021 - present' },
  decorators: [
    (Story) => (
      <div className="detail-records" style={{ maxWidth: 280 }}>
        <Story />
      </div>
    ),
  ],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof DetailRecord>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const WithCreatorLinks: Story = {
  args: { label: 'Story' },
  render: (args) => {
    const onNavigate = () => {}
    return (
      <DetailRecord label={args.label}>
        <Creators values={['Aoi Mizuhara', 'Haru Takeda']} role="author" onNavigate={onNavigate} />
      </DetailRecord>
    )
  },
  play: async ({ canvas }) => {
    const link = await canvas.findByRole('link', { name: 'Aoi Mizuhara' })
    await expect(link).toHaveAttribute('href', '/creator/Aoi%20Mizuhara?role=author')
  },
}

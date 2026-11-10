import type { Meta, StoryObj } from '@storybook/react-vite'
import { MetadataSiteIcon } from './MetadataSiteIcon'

const meta = {
  component: MetadataSiteIcon,
  tags: ['ai-generated'],
  args: { site: 'anilist', monogram: 'AL', size: 24 },
} satisfies Meta<typeof MetadataSiteIcon>

export default meta
type Story = StoryObj<typeof meta>

export const SiteIcon: Story = {}

export const AllSites: Story = {
  render: (args) => (
    <div style={{ display: 'flex', gap: 12, alignItems: 'center' }}>
      {['mangabaka', 'anilist', 'myanimelist', 'mangaupdates', 'kitsu', 'mangadex'].map((site) => (
        <MetadataSiteIcon key={site} {...args} site={site} />
      ))}
    </div>
  ),
}

export const MonogramFallback: Story = {
  args: { site: 'some-unknown-site', monogram: 'SU' },
}

export const Sizes: Story = {
  render: (args) => (
    <div style={{ display: 'flex', gap: 12, alignItems: 'center' }}>
      {[12, 14, 24, 40].map((size) => (
        <MetadataSiteIcon key={size} {...args} size={size} />
      ))}
    </div>
  ),
}

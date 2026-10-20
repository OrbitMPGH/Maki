import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { catalogue } from '../../../.storybook/mocks/shared'
import { discoverPage } from '../../../.storybook/mocks/discover'
import { DiscoverSeedStrip } from './DiscoverSeedStrip'

const id = (n: number) => catalogue[n - 1].mangaBakaId

const meta = {
  component: DiscoverSeedStrip,
  tags: ['ai-generated'],
  args: { seedIds: [id(1), id(6), id(5), id(2)] },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverSeedStrip>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    const links = await canvas.findAllByRole('link')
    await waitFor(() => expect(links).toHaveLength(4))
    await waitFor(() => expect(links[0]).toHaveAttribute('href', '/series/1'))
  },
}

export const CustomLabel: Story = {
  args: { label: 'From your library' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('From your library')).toBeVisible()
  },
}

export const UnknownSeedsDropped: Story = {
  args: { seedIds: [id(8), 999999] },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByRole('link')).toHaveLength(1))
  },
}

export const NothingResolves: Story = {
  args: { seedIds: [999998, 999999] },
}

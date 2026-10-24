import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { catalogueRails, discoverPage } from '../../../.storybook/mocks/discover'
import { catalogue } from '../../../.storybook/mocks/shared'
import { DiscoverCatalogue } from './DiscoverCatalogue'

const owned = new Map(catalogue.map((c) => [String(c.mangaBakaId), c.id]))

const meta = {
  component: DiscoverCatalogue,
  tags: ['ai-generated'],
  args: {
    rails: catalogueRails,
    cols: { base: 2, sm: 3, md: 4, lg: 6 },
    seriesIdFor: (item) => owned.get(item.providerId) ?? null,
    onOpen: fn(),
    onShowMore: fn(),
  },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverCatalogue>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    const tabs = await canvas.findAllByRole('tab')
    await waitFor(() => expect(tabs[0]).toHaveAttribute('aria-selected', 'true'))
  },
}

export const SwitchFeed: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('tab', { name: 'Top rated' }))
    await waitFor(() => expect(canvas.getByRole('tab', { name: 'Top rated' })).toHaveAttribute('aria-selected', 'true'))
    await waitFor(() => expect(canvas.getByRole('tab', { name: 'Most popular' })).toHaveAttribute('aria-selected', 'false'))
  },
}

export const ShowMore: Story = {
  play: async ({ args, canvas, userEvent }) => {
    const [button] = await canvas.findAllByRole('button', { name: 'Show more' })
    await userEvent.click(button)
    await waitFor(() => expect(args.onShowMore).toHaveBeenCalledWith(expect.objectContaining({ key: 'popular' })))
  },
}

export const OpenTitle: Story = {
  play: async ({ args, canvas, userEvent }) => {
    const [card] = await canvas.findAllByRole('button', { name: /^View and add / })
    await userEvent.click(card)
    await waitFor(() => expect(args.onOpen).toHaveBeenCalled())
  },
}

export const SingleFeed: Story = {
  args: { rails: [catalogueRails[0]] },
}

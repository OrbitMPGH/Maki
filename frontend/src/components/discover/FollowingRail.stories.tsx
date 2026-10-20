import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../../.storybook/mocks/api'
import { discoverPage, resetTabState } from '../../../.storybook/mocks/discover'
import { catalogue } from '../../../.storybook/mocks/shared'
import { FollowingRail, creatorHref } from './FollowingRail'

const owned = new Map(catalogue.map((c) => [String(c.mangaBakaId), c.id]))

const meta = {
  component: FollowingRail,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  args: {
    enabled: true,
    limit: 40,
    seriesIdFor: (item) => owned.get(item.providerId) ?? null,
    onOpen: fn(),
    onShowMore: fn(),
  },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof FollowingRail>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByRole('heading', { name: 'New from creators you follow' })).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Show more' }))
    await waitFor(() => expect(args.onShowMore).toHaveBeenCalledWith(
      expect.objectContaining({ key: 'following', sort: 'newest', excludeOwned: true }),
    ))
  },
}

export const ManageCreators: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Manage' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByRole('link', { name: /Aoi Mizuhara/ })).toBeVisible()
    await waitFor(() => expect(within(dialog).getAllByRole('button', { name: /^Unfollow / })).toHaveLength(3))
  },
}

export const Unfollow: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Manage' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Unfollow Mio Tachibana' }))
    await waitFor(() => expect(within(dialog).getAllByRole('button', { name: /^Unfollow / })).toHaveLength(2))
  },
}

export const EverythingOwned: Story = {
  parameters: { msw: discoverPage(http.post(api('/recommendations/discover/feed'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/already in your library/)).toBeVisible()
  },
}

export const FollowingNobody: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/discover/following'), () => HttpResponse.json({ creators: [] }))),
  },
}

export const Loading: Story = {
  parameters: { msw: discoverPage(http.get(api('/recommendations/discover/following'), () => neverResolve())) },
}

export const Disabled: Story = {
  args: { enabled: false },
}

export const CreatorLinks: StoryObj = {
  render: () => (
    <ul>
      <li>{creatorHref({ name: 'Aoi Mizuhara' })}</li>
      <li>{creatorHref({ name: 'Studio Lark', role: 'studio' })}</li>
    </ul>
  ),
}

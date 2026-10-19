import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http } from 'msw'
import { layerHandlers } from '../../.storybook/mocks/shell/use'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { inboxHandlers, inboxItems, longInbox, readerInboxItems } from '../../.storybook/mocks/shell/inbox'
import type { InboxItem } from '../api/inbox'
import NotificationsPage from './NotificationsPage'

// The mutations in these stories change what the next GET returns, so each run builds its own inbox.
const withInbox = (items: InboxItem[], options?: { pageSize?: number }) =>
  layerHandlers(() => inboxHandlers(items, options))

const meta = {
  component: NotificationsPage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', router: { path: '/notifications', initialEntries: ['/notifications'] } },
  beforeEach: withInbox(inboxItems),
} satisfies Meta<typeof NotificationsPage>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('New chapters: Lanterns Over Kasumi')).toBeVisible()
    await expect(canvas.getByText('Download failed: Iron Petal Academy')).toBeVisible()
    await expect(canvas.getByRole('heading', { name: 'Today' })).toBeVisible()
  },
}

export const LoadMore: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('New chapters: Lanterns Over Kasumi')
    await expect(canvas.queryByText('Import list finished')).toBeNull()
    await userEvent.click(canvas.getByRole('button', { name: 'Load more' }))
    await expect(await canvas.findByText('Import list finished')).toBeVisible()
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'Load more' })).toBeNull())
  },
}

export const UnreadOnly: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('You reached level 12')
    await userEvent.click(canvas.getByRole('switch', { name: 'Unread only' }))
    // The new filter is a new query, so the list is a skeleton for a moment before the unread rows arrive.
    await expect(await canvas.findByText('Download failed: Iron Petal Academy')).toBeVisible()
    await expect(canvas.queryByText('You reached level 12')).toBeNull()
  },
}

export const DownloadsFilter: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('New chapters: Lanterns Over Kasumi')
    await userEvent.click(canvas.getByRole('tab', { name: 'Downloads' }))
    await waitFor(() => expect(canvas.queryByText('New chapters: Lanterns Over Kasumi')).toBeNull())
    await expect(canvas.getByText('Download failed: Iron Petal Academy')).toBeVisible()
    await expect(canvas.getByText('Downloaded: Summer at Shiokaze Station')).toBeVisible()
    await expect(canvas.queryByText('New chapters: Lanterns Over Kasumi')).toBeNull()
  },
}

export const FilterWithNoMatches: Story = {
  beforeEach: withInbox(inboxItems.slice(0, 3)),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('New chapters: Lanterns Over Kasumi')
    await userEvent.click(canvas.getByRole('tab', { name: 'Requests' }))
    await expect(await canvas.findByText('Nothing matches')).toBeVisible()
  },
}

// A category only narrows the pages already loaded, so with more to fetch the empty state says so.
export const OlderMayMatch: Story = {
  beforeEach: withInbox([...inboxItems.slice(0, 3), ...inboxItems.filter((i) => i.type === 'requestSubmitted')], {
    pageSize: 3,
  }),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('New chapters: Lanterns Over Kasumi')
    await userEvent.click(canvas.getByRole('tab', { name: 'Requests' }))
    await expect(await canvas.findByText('None in the latest notifications')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Load more' }))
    await expect(await canvas.findByText('New request from Rin')).toBeVisible()
  },
}

export const Empty: Story = {
  beforeEach: withInbox([]),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No notifications yet')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Mark all read' })).toBeDisabled()
  },
}

export const AllRead: Story = {
  beforeEach: withInbox(inboxItems.map((i) => ({ ...i, read: true }))),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('New chapters: Lanterns Over Kasumi')
    await expect(canvas.getByRole('button', { name: 'Mark all read' })).toBeDisabled()
    await userEvent.click(canvas.getByRole('switch', { name: 'Unread only' }))
    await expect(await canvas.findByText('Nothing matches')).toBeVisible()
  },
}

export const LongBacklog: Story = {
  beforeEach: withInbox(longInbox, { pageSize: 25 }),
}

export const LongText: Story = {
  beforeEach: withInbox([
    {
      ...inboxItems[0],
      title: 'New chapters: Grave Robber of the Seventh Hall and the Lanterns Over Kasumi crossover special edition volume',
      body: 'Chapters 142, 143, 144, 145, 146 and 147 are available from three sources, and two of them replaced files that were already downloaded in a lower quality tier.',
    },
    { ...inboxItems[1], coverUrl: null },
    { ...inboxItems[2], url: null },
  ]),
}

export const Loading: Story = {
  beforeEach: layerHandlers(() => [http.get(api('/inbox'), () => neverResolve())]),
}

export const LoadError: Story = {
  beforeEach: layerHandlers(() => [http.get(api('/inbox'), () => failWith(500))]),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Couldn't load your notifications")).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

export const Reader: Story = {
  parameters: { auth: 'reader' },
  beforeEach: withInbox(readerInboxItems),
  play: async ({ canvas }) => {
    await canvas.findByText('New chapters: Lanterns Over Kasumi')
    await expect(canvas.getByRole('tab', { name: 'Downloads' })).toBeVisible()
    await expect(canvas.queryByRole('tab', { name: 'System' })).toBeNull()
  },
}

export const MarkAllRead: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('New chapters: Lanterns Over Kasumi')
    const button = canvas.getByRole('button', { name: 'Mark all read' })
    await expect(button).toBeEnabled()
    await userEvent.click(button)
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Mark all read' })).toBeDisabled())
  },
}

export const DismissOne: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('New chapters: Lanterns Over Kasumi')
    const [first] = canvas.getAllByRole('button', { name: 'Dismiss notification' })
    await userEvent.click(first)
    await waitFor(() => expect(canvas.queryByText('New chapters: Lanterns Over Kasumi')).toBeNull())
    await expect(canvas.getByText('Downloaded: Summer at Shiokaze Station')).toBeVisible()
  },
}

export const ClearAll: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('New chapters: Lanterns Over Kasumi')
    await userEvent.click(canvas.getByRole('button', { name: 'Clear all' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog', {
      name: 'Clear all notifications?',
    })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Clear all' }))
    await expect(await canvas.findByText('No notifications yet')).toBeVisible()
  },
}

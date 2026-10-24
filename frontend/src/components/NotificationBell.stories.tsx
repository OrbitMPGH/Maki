import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, type HttpHandler } from 'msw'
import { layerHandlers } from '../../.storybook/mocks/shell/use'
import { api, neverResolve } from '../../.storybook/mocks/api'
import { inboxHandlers, inboxItems, longInbox } from '../../.storybook/mocks/shell/inbox'
import type { InboxItem } from '../api/inbox'
import { NotificationBell } from './NotificationBell'

const withInbox = (items: InboxItem[], extra: HttpHandler[] = []) =>
  layerHandlers(() => [...extra, ...inboxHandlers(items)])

const meta = {
  component: NotificationBell,
  tags: ['ai-generated'],
  parameters: { layout: 'centered' },
  decorators: [
    // The dropdown opens below and to the left of the bell, so leave it somewhere to go.
    (Story) => (
      <div style={{ minHeight: 560, minWidth: 420, display: 'flex', justifyContent: 'flex-end', alignItems: 'flex-start' }}>
        <Story />
      </div>
    ),
  ],
  beforeEach: withInbox(inboxItems),
} satisfies Meta<typeof NotificationBell>

export default meta
type Story = StoryObj<typeof meta>

export const Closed: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Notifications' })).toBeVisible()
    // Six of the fixture's rows are unread; the badge is its own query.
    await expect(await canvas.findByText('6')).toBeVisible()
  },
}

export const Open: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Notifications' }))
    // The popover renders in a portal on document.body.
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText('New chapters: Lanterns Over Kasumi')).toBeVisible())
    await expect(await body.findByText('Download failed: Iron Petal Academy')).toBeVisible()
    await expect(await body.findByRole('button', { name: 'See all notifications' })).toBeVisible()
  },
}

export const ManyUnread: Story = {
  beforeEach: withInbox(longInbox.map((i) => ({ ...i, read: false }))),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('99+')).toBeVisible()
  },
}

export const Empty: Story = {
  beforeEach: withInbox([]),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Notifications' }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText('Nothing yet')).toBeVisible())
  },
}

export const Loading: Story = {
  beforeEach: withInbox(inboxItems, [http.get(api('/inbox'), () => neverResolve())]),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Notifications' }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText('Loading…')).toBeVisible())
  },
}

export const MarkAllRead: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Notifications' }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('button', { name: 'Mark all read' }))
    // Zero unread removes both the link and the badge.
    await waitFor(() => expect(body.queryByRole('button', { name: 'Mark all read' })).toBeNull())
  },
}

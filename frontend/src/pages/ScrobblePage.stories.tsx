import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http } from 'msw'
import { api, failWith, minutesAgo, neverResolve } from '../../.storybook/mocks/api'
import {
  scrobbleStatus,
  scrobbleStatusDisconnected,
  scrobbleHandlers,
  scrobbleStatusMixed,
  scrobbleStatusQuiet,
} from '../../.storybook/mocks/insights'
import ScrobblePage from './ScrobblePage'

const meta = {
  component: ScrobblePage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', router: { initialEntries: ['/scrobble'] }, msw: scrobbleHandlers() },
} satisfies Meta<typeof ScrobblePage>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Scrobble' })).toBeVisible()
    await expect(await canvas.findByText('maki_reads')).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Needs review' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Recent syncs' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Activity log' })).toBeVisible()
    await expect(canvas.getByText('Not updated')).toBeVisible()
  },
}

/** One tracker connected, one waiting for sign-in, one rejecting the saved details, two not set up. */
export const MixedConnections: Story = {
  parameters: { msw: scrobbleHandlers(scrobbleStatusMixed) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Not connected yet.')).toBeVisible()
    await expect(canvas.getByText("Can't sign in with the saved details.")).toBeVisible()
    await expect((await canvas.findAllByText('Not set up yet.')).length).toBe(2)
    await expect(canvas.getByRole('button', { name: 'Connect' })).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Disconnect' })).toBeVisible()
  },
}

/** With no tracker connected, Sync now explains why it does nothing. */
export const NothingConnected: Story = {
  parameters: { msw: scrobbleHandlers(scrobbleStatusDisconnected) },
  play: async ({ canvas }) => {
    const sync = await canvas.findByRole('button', { name: 'Sync now' })
    await expect(sync).toHaveAttribute('aria-disabled', 'true')
    await expect(await canvas.findByText('Nothing needs review.')).toBeVisible()
    await expect(canvas.getByText('No syncs yet.')).toBeVisible()
    await expect(canvas.getByText('No activity yet.')).toBeVisible()
  },
}

export const Quiet: Story = {
  parameters: { msw: scrobbleHandlers(scrobbleStatusQuiet) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing needs review.')).toBeVisible()
    await expect(canvas.getByText('No syncs yet.')).toBeVisible()
  },
}

export const SyncRunning: Story = {
  parameters: {
    msw: [...scrobbleHandlers({ ...scrobbleStatus, running: true, lastSyncAt: minutesAgo(31), nextSyncAt: null })],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/sync running/)).toBeVisible()
  },
}

export const SyncNow: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Sync now' }))
    await expect((await within(canvasElement.ownerDocument.body).findAllByText('Sync started.')).length).toBeGreaterThan(0)
  },
}

export const AssigningAMatch: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [firstUse] = await canvas.findAllByRole('button', { name: 'Use' })
    await userEvent.click(firstUse)
    // Notifications outlive a story, so an earlier run's toast can still be on screen.
    await expect(
      (await within(canvasElement.ownerDocument.body).findAllByText('Matched and queued for the next sync.')).length,
    ).toBeGreaterThan(0)
  },
}

export const AssigningByPastedId: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [field] = await canvas.findAllByRole('textbox', { name: 'Paste series URL or id…' })
    await userEvent.type(field, '118201{Enter}')
    // Notifications outlive a story, so an earlier run's toast can still be on screen.
    await expect(
      (await within(canvasElement.ownerDocument.body).findAllByText('Matched and queued for the next sync.')).length,
    ).toBeGreaterThan(0)
    await waitFor(() => expect(field).toHaveValue(''))
  },
}

export const IgnoringAMatch: Story = {
  play: async ({ canvas, userEvent }) => {
    const [ignore] = await canvas.findAllByRole('button', { name: 'Ignore' })
    await userEvent.click(ignore)
    await waitFor(() => expect(ignore).toBeEnabled())
  },
}

/** The OAuth round trip lands back here with the result in the query string, shown once and then cleared. */
export const OAuthConnected: Story = {
  parameters: { router: { initialEntries: ['/scrobble?connected=AniList'] }, msw: scrobbleHandlers() },
  play: async ({ canvasElement }) => {
    await expect(await within(canvasElement.ownerDocument.body).findByText('AniList connected')).toBeVisible()
  },
}

export const OAuthFailed: Story = {
  parameters: { router: { initialEntries: ['/scrobble?error=access_denied'] }, msw: scrobbleHandlers() },
  play: async ({ canvasElement }) => {
    await expect(await within(canvasElement.ownerDocument.body).findByText('access_denied')).toBeVisible()
  },
}

/** A non-admin cannot set trackers up, so the cards say who can. */
export const ReaderView: Story = {
  parameters: { auth: 'reader', msw: scrobbleHandlers(scrobbleStatusMixed) },
  play: async ({ canvas }) => {
    await expect((await canvas.findAllByText('Not set up yet. An admin can add it in Settings.')).length).toBe(2)
    await expect(canvas.queryByRole('link', { name: 'Set up in Settings' })).toBeNull()
  },
}

export const AdminSeesSetupLinks: Story = {
  parameters: { msw: scrobbleHandlers(scrobbleStatusMixed) },
  play: async ({ canvas }) => {
    await expect((await canvas.findAllByRole('link', { name: 'Set up in Settings' })).length).toBe(2)
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/scrobble/status'), () => neverResolve())] },
}

export const LoadFailed: Story = {
  parameters: { msw: [http.get(api('/scrobble/status'), () => failWith(500, 'The scrobble service did not answer.'))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Couldn't load scrobbling")).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

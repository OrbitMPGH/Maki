import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http } from 'msw'
import { api, failWith, minutesAgo, neverResolve } from '../../.storybook/mocks/api'
import {
  scrobbleStatus,
  scrobbleStatusBackedOff,
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
  play: async ({ canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByRole('heading', { name: 'Scrobble' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Needs review' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Recent syncs' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Activity log' })).toBeVisible()
    await expect(await canvas.findByText('Not updated')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: /^AniList\W+Connected$/ }))
    await expect(await within(canvasElement.ownerDocument.body).findByText('maki_reads')).toBeVisible()
  },
}

/**
 * Six trackers across five series: the result most trackers share is one line with a count, and a
 * tracker that lags or failed is listed under it with what differs.
 */
export const RecentSyncsGroupedByResult: Story = {
  play: async ({ canvas, userEvent }) => {
    const table = (await canvas.findByRole('columnheader', { name: 'Result' })).closest('table')!
    await waitFor(() => expect(within(table).getAllByRole('row')).toHaveLength(6))
    const [lanterns, summer, ironPetal, clockwork, ninth] = within(table).getAllByRole('row').slice(1).map((r) => within(r))
    await expect(lanterns.getByText('4 trackers')).toBeVisible()
    await expect(lanterns.getByRole('img', { name: 'MangaUpdates' })).toBeVisible()
    await expect(lanterns.getByText('Not updated')).toBeVisible()
    await expect(lanterns.queryByText(/Kitsu answered 429: slow down./)).toBeNull()
    await userEvent.click(lanterns.getByRole('button', { name: 'Details' }))
    await expect(await lanterns.findByText(/Kitsu answered 429: slow down./)).toBeVisible()
    await expect(summer.getByText('All 5 trackers')).toBeVisible()
    await expect(summer.queryByRole('listitem')).toBeNull()
    await expect(ironPetal.getByText('2 trackers')).toBeVisible()
    await expect(ironPetal.getByText('2 behind')).toBeVisible()
    await expect(ironPetal.getAllByRole('listitem')).toHaveLength(1)
    await expect(clockwork.getByText('MyAnimeList')).toBeVisible()
    await expect(ninth.getByText('Both trackers')).toBeVisible()
  },
}

/** One tracker connected, one waiting for sign-in, one rejecting the saved details, two not set up. */
export const MixedConnections: Story = {
  parameters: { msw: scrobbleHandlers(scrobbleStatusMixed) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(() => expect(canvas.getAllByRole('button', { name: /\W+Not connected$/ })).toHaveLength(2))
    await waitFor(() => expect(canvas.getAllByRole('button', { name: /\W+Not configured$/ })).toHaveLength(2))
    await userEvent.click(await canvas.findByRole('button', { name: /^MyAnimeList\W+Not connected$/ }))
    await expect(await body.findByText('Not connected yet.')).toBeVisible()
    await expect(await body.findByRole('button', { name: 'Connect' })).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: /^Kitsu\W+Not connected$/ }))
    await expect(await body.findByText("Can't sign in with the saved details.")).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: /^AniList\W+Connected$/ }))
    await expect(await body.findByRole('button', { name: 'Disconnect' })).toBeVisible()
  },
}

/** A rate limited tracker says when it will be tried again, measured from the instant the server sent. */
export const TrackerBackedOff: Story = {
  parameters: { msw: scrobbleHandlers(scrobbleStatusBackedOff) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Kitsu\W+Not connected$/ }))
    await expect(await within(canvasElement.ownerDocument.body).findByText(/^Rate limited\. Retrying in \d+ hours\.$/)).toBeVisible()
  },
}

/** With no tracker connected, Sync now explains why it does nothing. */
export const NothingConnected: Story = {
  parameters: { msw: scrobbleHandlers(scrobbleStatusDisconnected) },
  play: async ({ canvas }) => {
    const sync = await canvas.findByRole('button', { name: 'Sync now' })
    await waitFor(() => expect(sync).toHaveAttribute('aria-disabled', 'true'))
    await expect(await canvas.findByText('Nothing needs review.')).toBeVisible()
    await expect(await canvas.findByText('No syncs yet.')).toBeVisible()
    await expect(await canvas.findByText('No activity yet.')).toBeVisible()
  },
}

export const Quiet: Story = {
  parameters: { msw: scrobbleHandlers(scrobbleStatusQuiet) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing needs review.')).toBeVisible()
    await expect(await canvas.findByText('No syncs yet.')).toBeVisible()
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
    const sync = await canvas.findByRole('button', { name: 'Sync now' })
    await waitFor(() => expect(sync).toHaveAttribute('aria-disabled', 'false'))
    await userEvent.click(sync)
    await waitFor(() => expect((within(canvasElement.ownerDocument.body).getAllByText('Sync started.')).length).toBeGreaterThan(0))
  },
}

export const AssigningAMatch: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [firstUse] = await canvas.findAllByRole('button', { name: 'Use' })
    await userEvent.click(firstUse)
    // Notifications outlive a story, so an earlier run's toast can still be on screen.
    await waitFor(() => expect(
      (within(canvasElement.ownerDocument.body).getAllByText('Matched and queued for the next sync.')).length,
    ).toBeGreaterThan(0))
  },
}

export const AssigningByPastedId: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [field] = await canvas.findAllByRole('textbox', { name: 'Paste series URL or id…' })
    await userEvent.type(field, '118201{Enter}')
    // Notifications outlive a story, so an earlier run's toast can still be on screen.
    await waitFor(() => expect(
      (within(canvasElement.ownerDocument.body).getAllByText('Matched and queued for the next sync.')).length,
    ).toBeGreaterThan(0))
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

/** A non-admin cannot set trackers up, so a tracker that is not set up says who can. */
export const ReaderView: Story = {
  parameters: { auth: 'reader', msw: scrobbleHandlers(scrobbleStatusMixed) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [unset] = await canvas.findAllByRole('button', { name: /\W+Not configured$/ })
    await userEvent.click(unset)
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText('Not set up yet. An admin can add it in Settings.')).toBeVisible()
    await expect(body.queryByRole('link', { name: 'Set up in Settings' })).toBeNull()
    await expect(canvas.queryByRole('link', { name: 'Set up trackers' })).toBeNull()
  },
}

export const AdminSeesSetupLinks: Story = {
  parameters: { msw: scrobbleHandlers(scrobbleStatusMixed) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByRole('link', { name: 'Set up trackers' })).toBeVisible()
    const [unset] = await canvas.findAllByRole('button', { name: /\W+Not configured$/ })
    await userEvent.click(unset)
    await expect(await within(canvasElement.ownerDocument.body).findByRole('link', { name: 'Set up in Settings' })).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/scrobble/status'), () => neverResolve())] },
}

export const LoadFailed: Story = {
  parameters: { msw: [http.get(api('/scrobble/status'), () => failWith(500, 'The scrobble service did not answer.'))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Couldn't load scrobbling")).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

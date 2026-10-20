import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { connectedConnections, disconnectedConnections } from '../../.storybook/mocks/insights'
import { TrackerSyncControls } from './TrackerSyncControls'

const saved = fn()

const meta = {
  component: TrackerSyncControls,
  tags: ['ai-generated'],
  parameters: {
    layout: 'padded',
    msw: [
      http.put(api('/scrobble/preferences/:service'), async ({ request }) => {
        const body = (await request.json()) as { reading: boolean; ratings: boolean; anime?: boolean }
        saved(body)
        return HttpResponse.json({ service: 'anilist', ...body, anime: body.anime ?? null })
      }),
    ],
  },
  args: { service: 'anilist', label: 'AniList', connection: connectedConnections[0] },
  beforeEach: () => saved.mockClear(),
} satisfies Meta<typeof TrackerSyncControls>

export default meta
type Story = StoryObj<typeof meta>

export const Connected: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: 'Scrobble reading' })).toBeChecked())
    await waitFor(() => expect(canvas.getByRole('switch', { name: /Use anime for recommendation taste/ })).toBeChecked())
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Import ratings' })).toBeEnabled())
  },
}

export const TogglingReading: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('switch', { name: 'Scrobble reading' }))
    await waitFor(() => expect(saved).toHaveBeenCalledWith({ reading: false, ratings: true, anime: true }))
  },
}

/** A tracker with no anime list gets no anime switch, and the request leaves that setting alone. */
export const WithoutAnimeList: Story = {
  args: { service: 'mangabaka', label: 'MangaBaka', connection: connectedConnections[3] },
  play: async ({ canvas, userEvent }) => {
    await expect(canvas.queryByRole('switch', { name: /anime/ })).toBeNull()
    await userEvent.click(await canvas.findByRole('switch', { name: 'Sync ratings' }))
    await waitFor(() => expect(saved).toHaveBeenCalledWith({ reading: true, ratings: false }))
  },
}

export const RatingsSyncOff: Story = {
  args: { service: 'mal', label: 'MyAnimeList', connection: connectedConnections[1] },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: 'Sync ratings' })).not.toBeChecked())
  },
}

export const NotConnected: Story = {
  args: { connection: disconnectedConnections[0] },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Import ratings' })).toBeDisabled())
    await expect(await canvas.findByText('Connect on the Scrobble page to import.')).toBeVisible()
  },
}

/** Before the scrobble status loads there is no connection to edit. */
export const StatusLoading: Story = {
  args: { connection: undefined },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: 'Scrobble reading' })).toBeDisabled())
  },
}

export const OpensRatingImport: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Import ratings' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Import ratings from AniList' })
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(await body.findByText('6 of 6 selected')).toBeVisible()
  },
}

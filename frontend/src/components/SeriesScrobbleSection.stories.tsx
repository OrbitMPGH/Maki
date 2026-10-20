import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import { scrobble, seriesMsw } from '../../.storybook/mocks/series'
import type { SeriesScrobbleDto } from '../api/types'
import { SeriesScrobbleSection } from './SeriesScrobbleSection'

const serving = (body: SeriesScrobbleDto) => seriesMsw(http.get(api('/series/:id/scrobble'), () => HttpResponse.json(body)))

const meta = {
  component: SeriesScrobbleSection,
  tags: ['ai-generated'],
  args: { seriesId: 1 },
  parameters: { layout: 'padded', msw: seriesMsw() },
} satisfies Meta<typeof SeriesScrobbleSection>

export default meta
type Story = StoryObj<typeof meta>

export const Mixed: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('AniList')).toBeVisible()
    await expect(await canvas.findByText('MyAnimeList')).toBeVisible()
  },
}

export const ReviewMatchLinksCandidate: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Use' }))[0])
    await expect(await within(canvasElement.ownerDocument.body).findByText('Linked to the tracker entry')).toBeVisible()
  },
}

export const ReviewIgnore: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Ignore' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText(/will be ignored/)).toBeVisible()
  },
}

export const ReviewManualLink: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Paste id or URL' }), '5521')
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Link' })).toBeEnabled())
  },
}

export const AllSynced: Story = {
  parameters: {
    msw: serving({
      ...scrobble,
      services: scrobble.services.map((s) => ({ ...s, connected: true, reviewReason: null, reviewCandidates: [], chapter: 131, volume: 14, status: 'CURRENT', syncedAt: '2026-10-19T06:00:00Z' })),
    }),
  },
}

export const SyncError: Story = {
  parameters: {
    msw: serving({
      ...scrobble,
      services: scrobble.services.map((s, i) => (i === 0 ? { ...s, error: 'The tracker rejected the token' } : s)),
    }),
  },
}

export const NoTrackersConnected: Story = {
  parameters: {
    msw: serving({ ...scrobble, services: scrobble.services.map((s) => ({ ...s, connected: false, reviewReason: null, reviewCandidates: [] })) }),
  },
}

export const NotConfigured: Story = {
  parameters: { msw: serving({ configured: false, matched: false, kavitaSeriesId: null, services: [] }) },
}

export const NotMatchedInKavita: Story = {
  parameters: { msw: serving({ ...scrobble, matched: false, kavitaSeriesId: null, services: [] }) },
}

export const Failed: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/scrobble'), () => failWith(500))) },
}

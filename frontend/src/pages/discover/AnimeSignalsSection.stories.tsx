import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import {
  animeSignals,
  animeSignalsOff,
  animeSignalsSyncing,
  discoverPage,
  resetTabState,
} from '../../../.storybook/mocks/discover'
import { AnimeSignalRow, AnimeSignalsSection, AnimeSignalsStatusLine } from './AnimeSignalsSection'

const meta = {
  component: AnimeSignalsSection,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  args: { onOpenList: fn() },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof AnimeSignalsSection>

export default meta
type Story = StoryObj<typeof meta>

const serve = (data: object) => discoverPage(http.get(api('/recommendations/anime-signals'), () => HttpResponse.json(data)))

export const Enabled: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Clockwork Tea House: The Animation')).toBeVisible()
  },
}

export const SwitchedOff: Story = {
  parameters: { msw: serve(animeSignalsOff) },
}

export const Syncing: Story = {
  parameters: { msw: serve(animeSignalsSyncing) },
}

export const NoTrackerConnected: Story = {
  parameters: {
    msw: serve({
      ...animeSignals,
      services: [],
      entries: [],
      counts: { total: 0, matched: 0, positive: 0, avoided: 0, neutral: 0, superseded: 0, unmatched: 0 },
      lastSyncAtUtc: null,
    }),
  },
}

export const InstanceDisabled: Story = {
  parameters: { msw: serve({ ...animeSignals, instanceEnabled: false, enabled: false }) },
}

export const Loading: Story = {
  parameters: { msw: discoverPage(http.get(api('/recommendations/anime-signals'), () => neverResolve())) },
}

export const LoadError: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/anime-signals'), () => failWith(500, 'Tracker sync is unavailable'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Could not load anime signals/)).toBeVisible()
  },
}

export const StatusLine: StoryObj = {
  render: () => <AnimeSignalsStatusLine data={animeSignals} />,
}

export const StatusLineSyncing: StoryObj = {
  render: () => <AnimeSignalsStatusLine data={animeSignalsSyncing} />,
}

export const Row: StoryObj = {
  render: () => (
    <div style={{ display: 'grid', gap: 8 }}>
      {animeSignals.entries.map((entry) => (
        <AnimeSignalRow key={entry.key} entry={entry} />
      ))}
    </div>
  ),
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByText('Iron Petal Academy')).not.toHaveLength(0))
  },
}

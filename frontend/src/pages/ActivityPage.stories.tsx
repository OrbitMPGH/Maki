import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import {
  awaitingImportItem,
  cutoffUnmetPage,
  longTitleItem,
  pausedAll,
  pausedSources,
  proposals,
  queueItems,
  queuePage,
  upgradeHistoryPage,
  upgradeSettings,
  upgradesSummary,
} from '../../.storybook/mocks/operations/queue'
import ActivityPage from './ActivityPage'

const meta = {
  component: ActivityPage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', router: { path: '/activity', initialEntries: ['/activity'] } },
} satisfies Meta<typeof ActivityPage>

export default meta
type Story = StoryObj<typeof meta>

const emptyPage = { items: [], total: 0, page: 1, pageSize: 25 }

/** The upgrades endpoints, pinned here so the tab's content does not depend on another area's mocks. */
const upgradesData = [
  http.get(api('/upgrades/summary'), () => HttpResponse.json(upgradesSummary)),
  http.get(api('/settings/upgrades'), () => HttpResponse.json(upgradeSettings)),
  http.get(api('/upgrades/proposals'), () => HttpResponse.json(proposals)),
  http.get(api('/upgrades/history'), () => HttpResponse.json(upgradeHistoryPage())),
  http.get(api('/upgrades/cutoff-unmet'), () => HttpResponse.json(cutoffUnmetPage())),
]
const upgradesTab = { router: { path: '/activity', initialEntries: ['/activity?tab=upgrades'] } }

export const Default: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect((canvas.getAllByText('Lanterns Over Kasumi')).length).toBeGreaterThan(0))
    await waitFor(() => expect(canvas.getAllByText('Needs review').length).toBeGreaterThan(0))
    await expect(await canvas.findByText('The source returned no pages')).toBeVisible()
    await expect(await canvas.findByText('History')).toBeVisible()
  },
}

export const EmptyQueue: Story = {
  parameters: {
    msw: [
      http.get(api('/queue'), () => HttpResponse.json(queuePage([]))),
      http.get(api('/queue/history'), () => HttpResponse.json(emptyPage)),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing in the queue')).toBeVisible()
    await expect(await canvas.findByText('No history yet')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: {
    msw: [
      http.get(api('/queue'), () => neverResolve()),
      http.get(api('/queue/history'), () => neverResolve()),
    ],
  },
}

export const QueueLoadFailed: Story = {
  parameters: {
    msw: [
      http.get(api('/queue'), () => failWith(500, 'The queue could not be read')),
      http.get(api('/queue/history'), () => failWith(500, 'The history could not be read')),
    ],
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect((canvas.getAllByText("Couldn't load this section")).length).toBeGreaterThan(0))
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Retry' }).length).toBeGreaterThan(0))
  },
}

export const AllDownloadsPaused: Story = {
  parameters: { msw: [http.get(api('/queue/pause'), () => HttpResponse.json(pausedAll))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Downloads paused')).toBeVisible()
    await waitFor(() => expect((canvas.getAllByText('Paused')).length).toBeGreaterThan(0))
  },
}

export const SourcesPaused: Story = {
  parameters: { msw: [http.get(api('/queue/pause'), () => HttpResponse.json(pausedSources))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Asura Scans paused')).toBeVisible()
  },
}

export const TruncatedList: Story = {
  parameters: {
    msw: [http.get(api('/queue'), () => HttpResponse.json(queuePage(queueItems, 1450)))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Showing \d+ of 1450 queued items/)).toBeVisible()
  },
}

export const LongTitles: Story = {
  parameters: {
    msw: [http.get(api('/queue'), () => HttpResponse.json(queuePage([longTitleItem, ...queueItems.slice(0, 4)])))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/The Unabridged Account of the Lamp Keepers/)).toBeVisible()
  },
}

export const ReaderView: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await waitFor(() => expect((canvas.getAllByText('Lanterns Over Kasumi')).length).toBeGreaterThan(0))
    await expect(canvas.queryByRole('button', { name: 'Pause downloads' })).not.toBeInTheDocument()
    await expect(canvas.queryByRole('button', { name: 'Clear queue' })).not.toBeInTheDocument()
    await expect(canvas.queryByRole('button', { name: 'Review' })).not.toBeInTheDocument()
  },
}

export const ClearQueueConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear queue' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog')
    await waitFor(() => expect(within(dialog).getByText(/Pending downloads will be removed/)).toBeVisible())
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Cancel' }))
    await waitFor(() => expect(body.queryByRole('dialog')).not.toBeInTheDocument())
  },
}

export const RemoveTorrentRelease: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const row = (await canvas.findByText(awaitingImportItem.releaseTitle!)).closest('tr')!
    await userEvent.click(await within(row).findByRole('button', { name: 'Remove from queue' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog')
    await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: "Don't grab this release again" })).toBeChecked())
  },
}

export const ReviewParkedDownload: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review import' })
    await waitFor(async () => expect(await within(dialog).findByText('Clockwork Tea House v01.cbz')).toBeVisible())
  },
}

export const HistoryPaging: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findAllByText('Lanterns Over Kasumi')
    await userEvent.click(await canvas.findByRole('button', { name: '2' }))
    await waitFor(() => expect(canvas.queryByRole('button', { name: '3' })).not.toBeInTheDocument())
  },
}

export const HistoryFailed: Story = {
  parameters: { msw: [http.get(api('/queue/history'), () => failWith(500, 'The history could not be read'))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The history could not be read')).toBeVisible()
  },
}

export const UpgradesTab: Story = {
  parameters: { ...upgradesTab, msw: upgradesData },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Recent upgrades')).toBeVisible()
    await expect(await canvas.findByText('Volume releases waiting for you')).toBeVisible()
    await expect(await canvas.findByText('Cutoff unmet')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Scan now' })).toBeVisible()
  },
}

export const UpcomingTab: Story = {
  parameters: { router: { path: '/activity', initialEntries: ['/activity?tab=upcoming'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Running late')).toBeVisible()
    await expect(await canvas.findByRole('tab', { name: 'Upcoming', selected: true })).toBeVisible()
  },
}

export const SwitchToUpcoming: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('tab', { name: 'Upcoming' }))
    await expect(await canvas.findByText('Further out')).toBeVisible()
  },
}

export const SwitchToUpgrades: Story = {
  parameters: { msw: upgradesData },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('tab', { name: /Upgrades/ }))
    await expect(await canvas.findByText('Recent upgrades')).toBeVisible()
  },
}

export const UpgradesScanRunning: Story = {
  parameters: {
    ...upgradesTab,
    msw: [
      http.get(api('/upgrades/summary'), () => HttpResponse.json({ ...upgradesSummary, scanRunning: true })),
      ...upgradesData.slice(1),
    ],
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Scan running…' })).toBeDisabled())
  },
}

export const UpgradesNothingYet: Story = {
  parameters: {
    ...upgradesTab,
    msw: [
      http.get(api('/upgrades/summary'), () =>
        HttpResponse.json({ ...upgradesSummary, lastScanDate: null, lastVolumeSearchDate: null, trashBytes: 0, trashFiles: 0 }),
      ),
      http.get(api('/upgrades/proposals'), () => HttpResponse.json([])),
      http.get(api('/upgrades/history'), () => HttpResponse.json(upgradeHistoryPage([]))),
      http.get(api('/upgrades/cutoff-unmet'), () =>
        HttpResponse.json({ rows: [], total: 0, page: 1, pageSize: 25 }),
      ),
      http.get(api('/settings/upgrades'), () => HttpResponse.json(upgradeSettings)),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No upgrades yet')).toBeVisible()
  },
}

export const UpgradesNoProfile: Story = {
  parameters: {
    ...upgradesTab,
    msw: [
      http.get(api('/upgrades/summary'), () => HttpResponse.json({ ...upgradesSummary, profilesConfigured: false })),
      ...upgradesData.slice(1),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No quality profile assigned yet')).toBeVisible()
  },
}

export const UpgradesLoading: Story = {
  parameters: { ...upgradesTab, msw: [http.get(api('/upgrades/summary'), () => neverResolve()), ...upgradesData.slice(1)] },
}

export const UpgradesLoadFailed: Story = {
  parameters: {
    ...upgradesTab,
    msw: [http.get(api('/upgrades/summary'), () => failWith(500, 'The upgrade summary failed')), ...upgradesData.slice(1)],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The upgrade summary failed')).toBeVisible()
  },
}

export const UpgradesReaderView: Story = {
  parameters: { ...upgradesTab, auth: 'reader', msw: upgradesData },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Recent upgrades')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Scan now' })).not.toBeInTheDocument()
  },
}

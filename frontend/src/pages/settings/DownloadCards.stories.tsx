import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith } from '../../../.storybook/mocks/api'
import {
  downloadSettings,
  indexerJackett,
  indexerProwlarr,
  readFileCleanup,
  sourceLanguages,
  upgradeSettings,
} from '../../../.storybook/mocks/settings/data'
import {
  DownloadClientCard,
  DownloadQueueSection,
  FlareSolverrCard,
  IndexerSection,
  ReadFileCleanupSection,
  SmartDownloadSection,
  SourceLanguageSection,
  SourcePrioritySection,
  UpgradeScanSection,
  VolumeReleasesSection,
} from './DownloadCards'

const meta = {
  component: DownloadQueueSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 860 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof DownloadQueueSection>

export default meta
type Story = StoryObj<typeof meta>

export const DownloadQueue: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Concurrent chapter downloads' })).toHaveValue('2'))
    await expect(canvas.getByRole('textbox', { name: 'Give up on a chapter after (minutes)' })).toHaveValue('120')
    await expect(canvas.queryByText('Unsaved')).toBeNull()
  },
}

export const DownloadQueueEditShowsSave: Story = {
  play: async ({ canvas, userEvent }) => {
    const field = await canvas.findByRole('textbox', { name: 'Concurrent chapter downloads' })
    await waitFor(() => expect(field).toHaveValue('2'))
    await userEvent.clear(field)
    await userEvent.type(field, '4')
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Discard' }))
    await waitFor(() => expect(field).toHaveValue('2'))
    await expect(canvas.queryByText('Unsaved')).toBeNull()
  },
}

export const DownloadQueueSave: Story = {
  play: async ({ canvas, userEvent }) => {
    const field = await canvas.findByRole('textbox', { name: 'Concurrent chapter downloads' })
    await waitFor(() => expect(field).toHaveValue('2'))
    await userEvent.clear(field)
    await userEvent.type(field, '3')
    await userEvent.click(await canvas.findByRole('button', { name: 'Save' }))
    await expect(await within(document.body).findByText('Saved')).toBeVisible()
  },
}

export const DownloadQueueSaveRejected: Story = {
  parameters: { msw: settingsMsw(http.put(api('/settings/download'), () => failWith(400, 'Concurrent downloads must be between 1 and 8.'))) },
  play: async ({ canvas, userEvent }) => {
    const field = await canvas.findByRole('textbox', { name: 'Concurrent chapter downloads' })
    await waitFor(() => expect(field).toHaveValue('2'))
    await userEvent.clear(field)
    await userEvent.type(field, '3')
    await userEvent.click(await canvas.findByRole('button', { name: 'Save' }))
    // The edit stays unsaved when the server refuses it.
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Save' })).toBeEnabled())
    await expect(canvas.getByText('Unsaved')).toBeVisible()
    await expect(within(document.body).queryByText('Saved')).toBeNull()
  },
}

export const DownloadQueueNoTimeout: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/settings/download'), () => HttpResponse.json({ ...downloadSettings, itemTimeoutMinutes: 0, bulkHoldThreshold: 0 }))),
  },
  play: async ({ canvas }) => {
    const timeout = await canvas.findByRole('textbox', { name: 'Give up on a chapter after (minutes)' })
    await waitFor(() => expect(timeout).toHaveValue('0'))
  },
}

export const SmartDownload: Story = {
  render: () => <SmartDownloadSection />,
  play: async ({ canvas }) => {
    const left = await canvas.findByRole('textbox', { name: 'Fetch more when unread chapters drop to' })
    await waitFor(() => expect(left).toHaveValue('5'))
    await expect(canvas.getByRole('textbox', { name: 'Chapters to fetch each time' })).toHaveValue('10')
  },
}

export const ReadFileCleanupOff: Story = {
  render: () => <ReadFileCleanupSection />,
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Delete read chapters for every series/ })
    await expect(toggle).not.toBeChecked()
  },
}

export const ReadFileCleanupOn: Story = {
  render: () => <ReadFileCleanupSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/readcleanup'), () => HttpResponse.json({ ...readFileCleanup, enabled: true, days: 14 }))),
  },
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Delete read chapters for every series/ })
    await waitFor(() => expect(toggle).toBeChecked())
    await expect(canvas.getByRole('textbox', { name: 'Days after reading' })).toHaveValue('14')
  },
}

export const Languages: Story = {
  render: () => <SourceLanguageSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('switch', { name: 'Enable English' })).toBeChecked()
    await expect(canvas.getByRole('switch', { name: 'Enable French' })).not.toBeChecked()
  },
}

export const LanguagesToggleMarksUnsaved: Story = {
  render: () => <SourceLanguageSection />,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('switch', { name: 'Enable French' }))
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
  },
}

export const LanguagesNoneEnabledBlocksSave: Story = {
  render: () => <SourceLanguageSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/sources/languages'), () => HttpResponse.json({ ...sourceLanguages, order: ['en'], disabled: [] })),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('switch', { name: 'Enable English' }))
    await expect(await canvas.findByText('At least one language must stay enabled.')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Save' })).toBeDisabled()
  },
}

export const Sources: Story = {
  render: () => <SourcePrioritySection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/of 8 sources enabled/)).toBeVisible()
    await expect(canvas.getByText(/Covers \d+ languages/)).toBeVisible()
  },
}

export const SourcesPickByQuality: Story = {
  render: () => <SourcePrioritySection />,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByLabelText('Pick a source by'))
    await userEvent.click(await within(document.body).findByRole('option', { name: 'Best measured quality' }))
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
  },
}

export const SourcesManageOpensModal: Story = {
  render: () => <SourcePrioritySection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Manage sources' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
  },
}

export const FlareSolverr: Story = {
  render: () => <FlareSolverrCard />,
  play: async ({ canvas }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.10:8191'))
  },
}

export const FlareSolverrTest: Story = {
  render: () => <FlareSolverrCard />,
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.10:8191'))
    await userEvent.click(canvas.getByRole('button', { name: 'Test' }))
    await expect(await within(document.body).findByText('FlareSolverr is reachable')).toBeVisible()
  },
}

export const FlareSolverrUnreachable: Story = {
  render: () => <FlareSolverrCard />,
  parameters: {
    msw: settingsMsw(http.post(api('/settings/flaresolverr/test'), () => failWith(502, 'Could not connect to http://192.168.1.10:8191.'))),
  },
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.10:8191'))
    await userEvent.click(canvas.getByRole('button', { name: 'Test' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Test' })).toBeEnabled())
    await expect(within(document.body).queryByText('FlareSolverr is reachable')).toBeNull()
  },
}

export const DownloadClient: Story = {
  render: () => <DownloadClientCard />,
  play: async ({ canvas }) => {
    const client = await canvas.findByLabelText('Client')
    await waitFor(() => expect(client).toHaveValue('qBittorrent'))
    await expect(canvas.getByRole('textbox', { name: 'Username' })).toHaveValue('maki')
  },
}

export const DownloadClientNotConfigured: Story = {
  render: () => <DownloadClientCard />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/downloadclient'), () =>
        HttpResponse.json({ kind: null, url: null, username: null, password: null, label: null, pathMapFrom: null, pathMapTo: null }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByLabelText('Client')).toHaveValue('')
  },
}

export const DownloadClientDelugeHasNoUsername: Story = {
  render: () => <DownloadClientCard />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/downloadclient'), () =>
        HttpResponse.json({ kind: 'deluge', url: 'http://192.168.1.20:8112', username: null, password: null, label: null, pathMapFrom: null, pathMapTo: null }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByLabelText('Client')).toHaveValue('Deluge'))
    await expect(canvas.queryByRole('textbox', { name: 'Username' })).toBeNull()
  },
}

export const UpgradeScan: Story = {
  render: () => <UpgradeScanSection />,
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Run the daily scan/ })
    await waitFor(() => expect(toggle).toBeChecked())
    await waitFor(() => expect(canvas.getByLabelText('Default profile')).toHaveValue('Balanced'))
  },
}

export const UpgradeScanOff: Story = {
  render: () => <UpgradeScanSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/upgrades'), () => HttpResponse.json({ ...upgradeSettings, enabled: false, defaultProfileId: null }))),
  },
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Run the daily scan/ })
    await waitFor(() => expect(toggle).not.toBeChecked())
  },
}

export const UpgradeScanNow: Story = {
  render: () => <UpgradeScanSection />,
  play: async ({ canvas, userEvent }) => {
    const button = await canvas.findByRole('button', { name: 'Scan now' })
    await waitFor(() => expect(button).toBeEnabled())
    await userEvent.click(button)
  },
}

export const VolumeReleases: Story = {
  render: () => <VolumeReleasesSection />,
  play: async ({ canvas }) => {
    const search = await canvas.findByRole('switch', { name: /^Search torrents for volume releases/ })
    await waitFor(() => expect(search).toBeChecked())
    await expect(canvas.getByRole('textbox', { name: 'Auto-grab size limit (MB)' })).toHaveValue('500')
  },
}

export const VolumeReleasesOff: Story = {
  render: () => <VolumeReleasesSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/upgrades'), () => HttpResponse.json({ ...upgradeSettings, volumeSearch: false }))),
  },
  play: async ({ canvas }) => {
    const search = await canvas.findByRole('switch', { name: /^Search torrents for volume releases/ })
    await waitFor(() => expect(search).not.toBeChecked())
  },
}

export const Indexer: Story = {
  render: () => <IndexerSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('checkbox', { name: 'Nyaa Mirror' })).toBeChecked()
    await expect(canvas.getByRole('checkbox', { name: 'Tokyo Archive (disabled in Prowlarr)' })).not.toBeChecked()
    await expect(canvas.getByRole('checkbox', { name: 'Fakebay' })).toBeChecked()
  },
}

export const IndexerNotConfigured: Story = {
  render: () => <IndexerSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/indexer'), () => HttpResponse.json({ ...indexerProwlarr, url: null, apiKey: null }))),
  },
  play: async ({ canvas }) => {
    const backend = await canvas.findByLabelText('Indexer', { selector: 'input' })
    await waitFor(() => expect(backend).toHaveValue('Prowlarr'))
    await expect(canvas.queryByRole('checkbox', { name: 'Nyaa Mirror' })).toBeNull()
  },
}

export const IndexerListFailed: Story = {
  render: () => <IndexerSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/indexer/indexers'), () => failWith(502, 'Prowlarr refused the API key.'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Could not load indexers from Prowlarr/)).toBeVisible()
  },
}

export const IndexerSwitchBackend: Story = {
  render: () => <IndexerSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/indexer'), ({ request }) => {
        const backend = new URL(request.url).searchParams.get('backend')
        return HttpResponse.json(backend === 'jackett' ? indexerJackett : indexerProwlarr)
      }),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByLabelText('Indexer', { selector: 'input' }))
    await userEvent.click(await within(document.body).findByRole('option', { name: 'Jackett' }))
    await expect(await canvas.findByText(/Saving switches the indexer to Jackett/)).toBeVisible()
  },
}

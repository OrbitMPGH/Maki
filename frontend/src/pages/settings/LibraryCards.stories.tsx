import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith } from '../../../.storybook/mocks/api'
import {
  dumpProgressDownloading,
  nameParsing,
  recommendationIndex,
  recycleBin,
} from '../../../.storybook/mocks/settings/data'
import {
  LibraryFilesSection,
  MetadataSection,
  NameParsingSection,
  NamingSection,
  NewSeriesDefaultsSection,
  RecommendationIndexSection,
  RecycleBinSection,
  RootFoldersSection,
} from './LibraryCards'

const meta = {
  component: RootFoldersSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 860 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof RootFoldersSection>

export default meta
type Story = StoryObj<typeof meta>

async function dialogOf(body: HTMLElement) {
  const dialog = await within(body).findByRole('dialog')
  await waitFor(() => expect(dialog).toBeVisible())
  return within(dialog)
}

export const RootFolders: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('/library/manga')).toBeVisible()
    await expect(await canvas.findByText('(inaccessible)')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Remove root folder' })).toHaveLength(3))
  },
}

export const RootFoldersEmpty: Story = {
  parameters: { msw: settingsMsw(http.get(api('/rootfolder'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('textbox', { name: 'Path' })).toBeVisible()
    await expect(canvas.queryByRole('table')).toBeNull()
  },
}

export const RootFolderAdd: Story = {
  play: async ({ canvas, userEvent }) => {
    const path = await canvas.findByRole('textbox', { name: 'Path' })
    await userEvent.type(path, '/library/new-shelf')
    await userEvent.click(await canvas.findByRole('button', { name: 'Add' }))
    await waitFor(() => expect(path).toHaveValue(''))
  },
}

export const RootFolderAddRejected: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/rootfolder'), () => failWith(400, 'That folder does not exist or Maki cannot write to it.'))),
  },
  play: async ({ canvas, userEvent }) => {
    const path = await canvas.findByRole('textbox', { name: 'Path' })
    await userEvent.type(path, '/nope')
    await userEvent.click(await canvas.findByRole('button', { name: 'Add' }))
    await waitFor(() => expect(path).toHaveValue('/nope'))
  },
}

export const RootFolderRemoveConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [first] = await canvas.findAllByRole('button', { name: 'Remove root folder' })
    await userEvent.click(first)
    const dialog = await dialogOf(canvasElement.ownerDocument.body)
    await expect(await dialog.findByText('/library/manga')).toBeVisible()
    await expect(await dialog.findByRole('button', { name: 'Remove folder' })).toBeVisible()
  },
}

export const Metadata: Story = {
  render: () => <MetadataSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Snapshot on disk: 1.7 GB/)).toBeVisible()
    await expect(canvas.queryByRole('switch', { name: /local MangaBaka database/ })).toBeNull()
  },
}

export const MetadataDownloading: Story = {
  render: () => <MetadataSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/metadata/dump-progress'), () => HttpResponse.json(dumpProgressDownloading))),
  },
  play: async ({ canvas }) => {
    const refresh = await canvas.findByRole('button', { name: 'Refresh now' })
    await waitFor(() => expect(refresh).toBeDisabled())
  },
}

export const MetadataNoSnapshotYet: Story = {
  render: () => <MetadataSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/metadata'), () =>
        HttpResponse.json({ dumpPresent: false, dumpSizeBytes: null, dumpRefreshedAt: null, dumpDownloadFailed: false }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No snapshot downloaded yet')).toBeVisible()
  },
}

export const MetadataLastDownloadFailed: Story = {
  render: () => <MetadataSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/metadata/dump-progress'), () =>
        HttpResponse.json({ ...dumpProgressDownloading, running: false, lastError: 'The server closed the connection' }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Last download failed: The server closed the connection/)).toBeVisible()
  },
}

export const MetadataRefreshNow: Story = {
  render: () => <MetadataSection />,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Refresh now' }))
    await expect(await within(document.body).findByText(/Refresh started/)).toBeVisible()
  },
}

export const RecommendationIndex: Story = {
  render: () => <RecommendationIndexSection />,
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: /^Semantic search and recommendations/ })).toBeChecked())
  },
}

export const RecommendationIndexBuilding: Story = {
  render: () => <RecommendationIndexSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/recommendations'), () =>
        HttpResponse.json({ ...recommendationIndex, running: true, phase: 'embedding', scanned: 61_000, embedded: 61_000, estimatedSecondsRemaining: 540 }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('progressbar')).toBeVisible()
  },
}

export const RecommendationIndexOff: Story = {
  render: () => <RecommendationIndexSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/recommendations'), () => HttpResponse.json({ ...recommendationIndex, embeddingModel: 'off', vectorCount: 0 })),
    ),
  },
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Semantic search and recommendations/ })
    await waitFor(() => expect(toggle).not.toBeChecked())
  },
}

export const NewSeriesDefaults: Story = {
  render: () => <NewSeriesDefaultsSection />,
  play: async ({ canvas }) => {
    const pornographic = await canvas.findByLabelText('Incognito for Pornographic')
    await waitFor(() => expect(pornographic).toHaveValue('Full'))
    await waitFor(() => expect(canvas.getByLabelText('Incognito for Safe')).toHaveValue('Off'))
  },
}

export const NewSeriesDefaultsSkipSpecials: Story = {
  render: () => <NewSeriesDefaultsSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/monitoring'), () => HttpResponse.json({ unmonitorSpecials: true, defaultMode: 'All' }))),
  },
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Skip specials/ })
    await waitFor(() => expect(toggle).toBeChecked())
  },
}

export const LibraryFiles: Story = {
  render: () => <LibraryFilesSection />,
  play: async ({ canvas, userEvent }) => {
    const cover = await canvas.findByRole('switch', { name: /^Save a cover.jpg/ })
    await waitFor(() => expect(cover).not.toBeChecked())
    await userEvent.click(cover)
    await waitFor(() => expect(cover).toBeChecked())
  },
}

export const Naming: Story = {
  render: () => <NamingSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Example: Lanterns Over Kasumi (2021)')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('radio', { name: 'Rename the folder to the series folder format' })).toBeChecked())
  },
}

export const NamingInvalidFormat: Story = {
  render: () => <NamingSection />,
  play: async ({ canvas, userEvent }) => {
    const folder = await canvas.findByRole('textbox', { name: 'Series folder format' })
    await waitFor(() => expect(folder).toHaveValue('{Series TitleYear}'))
    await userEvent.clear(folder)
    await userEvent.type(folder, '{{Series Title')
    await expect(await canvas.findByText('The token is not closed.', {}, { timeout: 4000 })).toBeVisible()
  },
}

export const NamingRenameAllConfirm: Story = {
  render: () => <NamingSection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    const button = await canvas.findByRole('button', { name: 'Rename every series to current format' })
    await waitFor(() => expect(button).toBeEnabled())
    await userEvent.click(button)
    const dialog = await dialogOf(canvasElement.ownerDocument.body)
    await expect(await dialog.findByRole('button', { name: 'Rename all' })).toBeVisible()
  },
}

export const NameParsing: Story = {
  render: () => <NameParsingSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Chapters: Chapitre, Ch')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('checkbox', { name: /^Fran/ })).toBeChecked())
    await waitFor(() => expect(canvas.getByRole('checkbox', { name: 'Deutsch' })).not.toBeChecked())
  },
}

export const NameParsingToggle: Story = {
  render: () => <NameParsingSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/nameparsing'), () => HttpResponse.json({ ...nameParsing, languages: [] }))),
  },
  play: async ({ canvas, userEvent }) => {
    const spanish = await canvas.findByRole('checkbox', { name: /^Espa/ })
    await userEvent.click(spanish)
    await expect(spanish).toBeInTheDocument()
  },
}

export const RecycleBinWithFiles: Story = {
  render: () => <RecycleBinSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Lanterns Over Kasumi - c131.cbz')).toBeVisible()
    await expect(await canvas.findByText('(no longer in the library)')).toBeVisible()
    await expect(await canvas.findByText('The file is missing from the bin folder.')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Restore' })[2]).toBeDisabled())
  },
}

export const RecycleBinEmpty: Story = {
  render: () => <RecycleBinSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/recyclebin'), () => HttpResponse.json({ ...recycleBin, entries: [], totalBytes: 0 }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The recycle bin is empty.')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Empty bin' })).toBeDisabled())
  },
}

export const RecycleBinNonAdmin: Story = {
  render: () => <RecycleBinSection />,
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Lanterns Over Kasumi - c131.cbz')).toBeVisible()
    await expect(canvas.queryByRole('textbox', { name: /Keep deleted files/ })).toBeNull()
  },
}

export const RecycleBinDeletePermanently: Story = {
  render: () => <RecycleBinSection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [first] = await canvas.findAllByRole('button', { name: 'Delete permanently' })
    await userEvent.click(first)
    const dialog = await dialogOf(canvasElement.ownerDocument.body)
    await expect(await dialog.findByText('This action cannot be undone.')).toBeVisible()
  },
}

export const RecycleBinEmptyConfirm: Story = {
  render: () => <RecycleBinSection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    const empty = await canvas.findByRole('button', { name: 'Empty bin' })
    await waitFor(() => expect(empty).toBeEnabled())
    await userEvent.click(empty)
    const dialog = await dialogOf(canvasElement.ownerDocument.body)
    await expect(await dialog.findByText('Empty the recycle bin?')).toBeVisible()
  },
}

export const RecycleBinRetentionEdit: Story = {
  render: () => <RecycleBinSection />,
  play: async ({ canvas, userEvent }) => {
    const days = await canvas.findByRole('textbox', { name: /Keep deleted files/ })
    await userEvent.clear(days)
    await userEvent.type(days, '30')
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { adminMe, api, signedInAs } from '../../.storybook/mocks/api'
import { rootFolders } from '../../.storybook/mocks/operations/common'
import { scanCandidates } from '../../.storybook/mocks/operations/import'
import ImportPage from './ImportPage'

const meta = {
  component: ImportPage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', router: { path: '/import', initialEntries: ['/import'] } },
} satisfies Meta<typeof ImportPage>

export default meta
type Story = StoryObj<typeof meta>

type Canvas = ReturnType<typeof within>

/** Picks the first root folder and scans it. */
async function scan(canvas: Canvas, body: Canvas, userEvent: { click: (element: Element) => Promise<void> }) {
  await userEvent.click(await canvas.findByRole('combobox', { name: 'Root folder' }))
  await userEvent.click(await body.findByRole('option', { name: '/library/manga' }))
  await userEvent.click(await canvas.findByRole('button', { name: 'Scan' }))
  await canvas.findByText('Moth Orchard')
}

export const BeforeScanning: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('combobox', { name: 'Root folder' })).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Scan' })).toBeDisabled())
    await expect(await canvas.findByText('Choose a root folder')).toBeVisible()
  },
}

export const NoRootFolders: Story = {
  parameters: { msw: [http.get(api('/rootfolder'), () => HttpResponse.json([]))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No root folders yet')).toBeVisible()
  },
}

export const ScanResults: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await scan(canvas, body, userEvent)
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Import 3 selected' })).toBeEnabled())
    await expect(await canvas.findByText('searched as “Starlit Ferry”')).toBeVisible()
    await expect(await canvas.findByText('8 unrecognized')).toBeVisible()
    await expect(await canvas.findByText('No metadata match found.')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: '3 ignored folders' })).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('switch', { name: /Show 1 series already in the library/ })).not.toBeChecked())
  },
}

export const ShowSeriesAlreadyInLibrary: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await scan(canvas, body, userEvent)
    await userEvent.click(await canvas.findByRole('switch', { name: /Show 1 series already in the library/ }))
    await expect(await canvas.findByText('In library')).toBeVisible()
  },
}

export const RestoreAnArchivedSeries: Story = {
  parameters: {
    msw: [
      http.get(api('/libraryimport/scan'), () =>
        HttpResponse.json(scanCandidates.filter((c) => c.kind === 'restore')),
      ),
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('combobox', { name: 'Root folder' }))
    await userEvent.click(await body.findByRole('option', { name: '/library/manga' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Scan' }))
    await expect(await canvas.findByText('Restore Lantern Keepers')).toBeVisible()
    await expect(canvas.queryByRole('switch', { name: /already in the library/ })).not.toBeInTheDocument()
    await userEvent.click(await canvas.findByRole('checkbox', { name: 'Restore Lantern Keepers' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Import 1 selected' }))
    const dialog = await body.findByRole('dialog', { name: 'Import 1 folder?' })
    await expect(await within(dialog).findByRole('button', { name: 'Restore' })).toBeVisible()
  },
}

export const RestoreNeedsArchivePermission: Story = {
  parameters: {
    msw: [
      signedInAs({
        ...adminMe,
        isAdmin: false,
        permissionNames: adminMe.permissionNames.filter((p) => p !== 'ArchiveSeries'),
      }),
      http.get(api('/libraryimport/scan'), () =>
        HttpResponse.json(scanCandidates.filter((c) => c.kind === 'restore')),
      ),
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('combobox', { name: 'Root folder' }))
    await userEvent.click(await body.findByRole('option', { name: '/library/manga' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Scan' }))
    await canvas.findByRole('checkbox', { name: 'Restore Lantern Keepers' })
    await waitFor(() => expect(canvas.getByRole('checkbox', { name: 'Restore Lantern Keepers' })).toBeDisabled())
  },
}

export const NothingToImport: Story = {
  parameters: { msw: [http.get(api('/libraryimport/scan'), () => HttpResponse.json([]))] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('combobox', { name: 'Root folder' }))
    await userEvent.click(await body.findByRole('option', { name: '/library/manga' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Scan' }))
    await expect(await canvas.findByText('Nothing to import')).toBeVisible()
  },
}

export const PreviewSelected: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await scan(canvas, body, userEvent)
    await userEvent.click(await canvas.findByRole('button', { name: 'Preview 3 selected' }))
    const dialog = await body.findByRole('dialog', { name: 'Import preview' })
    await waitFor(() => expect(within(dialog).getAllByText('Nothing has been changed. This is what importing would do.')).toHaveLength(1))
  },
}

export const ConfirmAndImport: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await scan(canvas, body, userEvent)
    await userEvent.click(await canvas.findByRole('button', { name: 'Import 3 selected' }))
    const dialog = await body.findByRole('dialog', { name: 'Import 3 folders?' })
    await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: /Standardize ComicInfo.xml/ })).toBeChecked())
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Import' }))
    await expect(await canvas.findByText(/Starlit Ferry \(2023\): added 41 files, linking once sources are found/)).toBeVisible()
    await expect(await canvas.findByText(/The folder is read-only, nothing was changed\./)).toBeVisible()
    await expect(await canvas.findByText('Cover could not be downloaded, using the first page instead.')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Undo this import' })).toBeVisible()
  },
}

export const FindAMatchByHand: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await scan(canvas, body, userEvent)
    await userEvent.click(await canvas.findByRole('button', { name: 'Find match' }))
    const dialog = await body.findByRole('dialog', { name: /Find a match for zz_unsorted_scans_2019/ })
    const option = await within(dialog).findByRole('button', { name: /^Paper Crane Cartel\s?\d{4}$/ }, { timeout: 4000 })
    await userEvent.click(option)
    await waitFor(() => expect(body.queryByRole('dialog')).not.toBeInTheDocument())
    await expect(await canvas.findByRole('button', { name: 'Import 4 selected' })).toBeVisible()
  },
}

export const IgnoreAFolder: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await scan(canvas, body, userEvent)
    await userEvent.click(await canvas.findByRole('button', { name: 'Ignore Moth Orchard' }))
    await waitFor(() => expect(canvas.queryByText('Moth Orchard')).not.toBeInTheDocument())
  },
}

export const IgnoredFolders: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await scan(canvas, body, userEvent)
    await userEvent.click(await canvas.findByRole('button', { name: '3 ignored folders' }))
    const dialog = await body.findByRole('dialog', { name: 'Ignored folders' })
    await waitFor(async () => expect(await within(dialog).findByText('_unsorted')).toBeVisible())
  },
}

export const RecentImportsListed: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('combobox', { name: 'Root folder' }))
    await userEvent.click(await body.findByRole('option', { name: rootFolders[0].path }))
    await expect(await canvas.findByText('Recent imports')).toBeVisible()
    await expect(await canvas.findByText('Imports can be undone for 30 days.')).toBeVisible()
  },
}

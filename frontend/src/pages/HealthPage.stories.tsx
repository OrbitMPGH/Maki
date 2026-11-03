import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import type { FileDetail } from '../api/health'
import {
  errorChecks,
  groupedChecks,
  groupOperations,
  damagedIndexedDetail,
  groupRows,
  keptDuplicateDetail,
  manyFailedDeletions,
  missingMovedDetail,
  missingWeakDetail,
  overview,
  relinkDeleteDetail,
  sizeMismatchDetail,
  spareDuplicateDetail,
  runningScan,
  unlinkedNoSeries,
  unreadableDetail,
  unlinkedUnrecognized,
  verifyScan,
  warningChecks,
} from '../../.storybook/mocks/operations/health'
import { AfterSignIn } from '../../.storybook/AfterSignIn'
import { monoText } from '../../.storybook/monoText'
import HealthPage from './HealthPage'

const meta = {
  component: HealthPage,
  tags: ['ai-generated'],
  decorators: [(Story) => <AfterSignIn><Story /></AfterSignIn>],
  parameters: { layout: 'fullscreen', router: { path: '/health', initialEntries: ['/health'] } },
} satisfies Meta<typeof HealthPage>

export default meta
type Story = StoryObj<typeof meta>

const withOverview = (over: Parameters<typeof overview>[0]) => http.get(api('/health'), () => HttpResponse.json(overview(over)))
const tab = (name: string) => ({ router: { path: '/health', initialEntries: [`/health?tab=${name}`] } })
const fileDetail = (detail: FileDetail) => http.get(api('/health/files/:id'), () => HttpResponse.json(detail))
const emptyList = { items: [], total: 0 }

export const Healthy: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('System checks')).toBeVisible()
    await expect(await canvas.findByText(/Everything is passing/)).toBeVisible()
    await expect(await canvas.findByText('Image cache and backups')).toBeVisible()
    await expect(await canvas.findByRole('link', { name: 'Health settings' })).toHaveAttribute(
      'href',
      '/settings?tab=system&s=health',
    )
  },
}

export const ShowPassingChecks: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('switch', { name: 'Show passing' }))
    await expect(await canvas.findByText('The database passed its integrity check')).toBeVisible()
    await expect(await canvas.findByText('/library/manga has 842 GB free (71%)')).toBeVisible()
  },
}

export const Warnings: Story = {
  parameters: { msw: [withOverview({ checks: warningChecks, openFindings: 4 })] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('/library/manhwa has 38 GB free (9%), below the 10% warning level')).toBeVisible()
    await expect(await canvas.findByText('3 need attention')).toBeVisible()
    await expect(await canvas.findByText(/Acknowledged, hidden from the header badge/)).toBeVisible()
  },
}

export const Errors: Story = {
  parameters: { msw: [withOverview({ checks: errorChecks, openFindings: 12 })] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The database reported a failed integrity check')).toBeVisible()
    await expect(await canvas.findByText('5 need attention')).toBeVisible()
  },
}

export const AcknowledgeAWarning: Story = {
  parameters: { msw: [withOverview({ checks: warningChecks })] },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Acknowledge' }))[0])
    await expect(await canvas.findByText('System checks')).toBeVisible()
  },
}

export const GroupedWarnings: Story = {
  parameters: { msw: [withOverview({ checks: groupedChecks })] },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('4 monitored series have no enabled sources')).toBeVisible()
    await expect(await canvas.findByText('3 series are failing to refresh from their sources')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Show all 4 checks' }))
    await expect(await canvas.findByText('Bluefin Boys is monitored but has no enabled sources')).toBeVisible()
  },
}

export const SourceWithAffectedSeries: Story = {
  parameters: { msw: [withOverview({ checks: groupedChecks })] },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Show 3 affected series' }))
    await expect(await canvas.findByText('HTTP 403 while loading the chapter list')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Refresh all' })).toBeEnabled())
  },
}

export const ScanRunning: Story = {
  parameters: { msw: [withOverview({ scans: [runningScan, verifyScan], openFindings: 2 })] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Index scan 7: running')).toBeVisible()
    await expect(await canvas.findByText('Verify scan 8: pending')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Cancel scan' })).toHaveLength(2))
  },
}

export const PartialScanError: Story = {
  parameters: {
    msw: [withOverview({ scans: [{ id: 9, status: 'partial', completed: 3100, total: 6200, verify: true, error: 'The scan stopped when /library/manhwa went offline' }] })],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Last partial scan: The scan stopped/)).toBeVisible()
  },
}

export const NoChecksYet: Story = {
  parameters: { msw: [withOverview({ checks: [], files: 0 })] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No checks have run yet. Use Check now.')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/health'), () => neverResolve())] },
}

export const LoadFailed: Story = {
  parameters: { msw: [http.get(api('/health'), () => failWith(500, 'The health monitor is not running'))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The health monitor is not running')).toBeVisible()
  },
}

export const ScanMenu: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Scan files' }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByRole('menuitem', { name: /Index/ })).toBeVisible())
    await waitFor(() => expect(body.getByRole('menuitem', { name: /Verify/ })).toBeVisible())
  },
}

export const RebuildImageCache: Story = {
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('Missing posters')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Rebuild missing images' }))
    await expect(await canvas.findByRole('link', { name: 'Backup and cache tools' })).toBeVisible()
  },
}

export const FilesTab: Story = {
  parameters: tab('files'),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(monoText('Lanterns Over Kasumi/Lanterns Over Kasumi - Ch.57.cbz'))).toBeVisible()
    await waitFor(() => expect(canvas.getAllByText('Not linked').length).toBeGreaterThan(0))
  },
}

export const FilesSearch: Story = {
  parameters: tab('files'),
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Search file paths' }), 'Starlit')
    await waitFor(() => expect(canvas.queryByText(monoText(/Lanterns Over Kasumi\/Lanterns/))).not.toBeInTheDocument())
    await expect(await canvas.findByText(monoText('Starlit Ferry/Starlit Ferry - Ch.4.cbz'))).toBeVisible()
  },
}

export const FilesEmpty: Story = {
  parameters: { ...tab('files'), msw: [http.get(api('/health/files'), () => HttpResponse.json(emptyList))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No files match these filters. Run a scan to inventory the library.')).toBeVisible()
  },
}

export const FilesLoading: Story = {
  parameters: { ...tab('files'), msw: [http.get(api('/health/files'), () => neverResolve())] },
}

export const FilesLoadFailed: Story = {
  parameters: { ...tab('files'), msw: [http.get(api('/health/files'), () => failWith(500, 'The inventory could not be read'))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The inventory could not be read')).toBeVisible()
  },
}

export const BulkSelectAndDelete: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('checkbox', { name: 'Select every file on this page' }))
    await expect(await canvas.findByText('5 selected')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Delete' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Delete 5 archives?' })
    const confirm = await within(dialog).findByRole('button', { name: 'Delete permanently' })
    await waitFor(() => expect(confirm).toBeDisabled())
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: 'I confirm permanent deletion of 5 archives.' }))
    await userEvent.click(confirm)
    await expect(await canvas.findByText('4 deleted, 1 refused:')).toBeVisible()
  },
}

export const ReviewDamagedArchive: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await expect(await within(dialog).findByText('Archive is damaged')).toBeVisible()
    await waitFor(async () => expect((await within(dialog).findAllByText('Page 12 failed its CRC check'))[0]).toBeVisible())
    const fetch = await within(dialog).findByRole('button', { name: 'Get a new copy' })
    await waitFor(() => expect(fetch).toBeEnabled())
    await expect(within(dialog).queryByRole('button', { name: 'Download candidate for review' })).toBeNull()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Other actions' }))
    await waitFor(async () => expect(await within(dialog).findByText('Request replacement', { selector: 'h3' })).toBeVisible())
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Download candidate for review' })).toBeEnabled())
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Review permanent deletion' })).toBeVisible())
  },
}

export const ReviewUnlinkedArchive: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[1])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await waitFor(async () => expect(await within(dialog).findByText('Not linked to any chapter')).toBeVisible())
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Import this archive' })).toBeVisible())
  },
}

export const ReviewDuplicateAgainstLinkedFile: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[2])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await waitFor(async () => expect(await within(dialog).findByText('This archive')).toBeVisible())
    await waitFor(() => expect(within(dialog).getByText('Linked file')).toBeVisible())
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Review linked file' })).toBeVisible())
  },
}

const filterBy = async (
  canvas: { findByRole: (role: string, options: { name: string }) => Promise<HTMLElement> },
  body: ReturnType<typeof within>,
  click: (el: HTMLElement) => Promise<void>,
  finding: string,
) => {
  await click(await canvas.findByRole('combobox', { name: 'Filter by finding' }))
  await click(await body.findByRole('option', { name: finding }))
}
const filterDuplicates = (
  canvas: Parameters<typeof filterBy>[0],
  body: ReturnType<typeof within>,
  click: (el: HTMLElement) => Promise<void>,
) => filterBy(canvas, body, click, 'Duplicate')

/** The Missing filter: a file found at a new path relinks in one click, a vanished folder clears or downloads as a group. */
export const FilesMissingFilter: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await filterBy(canvas, body, (el) => userEvent.click(el), 'Missing')
    await expect(await canvas.findByText('1 file found at a new path')).toBeVisible()
    await expect(await canvas.findByText('The Ninth Kitchen (2021)/The Ninth Kitchen - Ch.61.cbz')).toBeVisible()
    await expect(await canvas.findByText('2 files gone, nowhere else in the library')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Relink it' }))
    await expect(await body.findByText('Relinked 1 file')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear records' }))
    await expect(await body.findByRole('dialog', { name: 'Delete 2 archives?' })).toBeVisible()
  },
}

/** The Unlinked filter sorted by what each file needs; the ready ones import in one click. */
export const FilesUnlinkedCases: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await filterBy(canvas, body, (el) => userEvent.click(el), 'Not linked')
    await expect(await canvas.findByText(/Nothing holds these chapters yet/)).toBeVisible()
    await expect(await canvas.findByText(/No series owns the folder these sit in/)).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Import it' }))
    await expect(await body.findByText('Imported 1 archive')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Show only: Chapter already has a file' }))
    await waitFor(() => expect(canvas.queryByText(monoText('Starlit Ferry/Starlit Ferry - Ch.4.cbz'))).toBeNull())
    await expect(await canvas.findByText(monoText('Clockwork Tea House/Clockwork Tea House - Ch.18.cbz'))).toBeVisible()
  },
}

/** A selection with damaged files offers new copies for them, and only them. */
export const BulkGetNewCopies: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('checkbox', { name: 'Select every file on this page' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Get a new copy' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText('Requested 1 replacement')).toBeVisible()
  },
}

/** The Duplicate filter: one row per set of copies, the kept one leading, and a bar to delete every spare. */
export const FilesDuplicateGroups: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(keptDuplicateDetail)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await filterDuplicates(canvas, body, (el) => userEvent.click(el))
    await expect(await canvas.findByText(monoText('Ashen Crown Regression/Ashen Crown Regression v10.cbz'))).toBeVisible()
    await expect(
      await canvas.findByRole('button', { name: 'Review Ashen Crown Regression/Ashen Crown Regression - Volume 10 (Digital).cbz' }),
    ).toBeVisible()
    await expect(await canvas.findByText(/1 spare copy is identical to a file that stays/)).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Delete the spare copy' }))
    const dialog = await body.findByRole('dialog', { name: 'Delete 1 spare copy?' })
    await expect(within(dialog).queryByRole('checkbox')).not.toBeInTheDocument()
    const confirm = await within(dialog).findByRole('button', { name: 'Delete spare copy' })
    await waitFor(() => expect(confirm).toBeEnabled())
  },
}

/** Selecting a set selects every copy, but delete only ever offers the spares. */
export const FilesDuplicateSelection: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await filterDuplicates(canvas, body, (el) => userEvent.click(el))
    await userEvent.click(
      await canvas.findByRole('checkbox', { name: 'Select every copy of Ashen Crown Regression/Ashen Crown Regression v10.cbz' }),
    )
    await expect(await canvas.findByText('2 selected')).toBeVisible()
    const remove = await canvas.findByRole('button', { name: 'Delete 1 spare copy' })
    await waitFor(() => expect(remove).toBeEnabled())
  },
}

/** Opened from the kept copy: the spare goes in two clicks, and the bigger actions wait behind a toggle. */
export const ReviewKeptDuplicate: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(keptDuplicateDetail)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await expect(await within(dialog).findByText('Identical copies')).toBeVisible()
    await expect(await within(dialog).findByText(/One other file is byte for byte the same as this one/)).toBeVisible()
    await expect(await within(dialog).findByText('Chapters read from it: Ch. 77–85')).toBeVisible()
    await expect(within(dialog).queryByRole('button', { name: 'Download candidate for review' })).not.toBeInTheDocument()

    await userEvent.click(await within(dialog).findByRole('button', { name: 'Delete the spare copy' }))
    await expect(await within(dialog).findByText(/Delete the spare copy from disk\? This file stays/)).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(within(dialog).queryByText(/Delete the spare copy from disk/)).not.toBeInTheDocument())

    await userEvent.click(await within(dialog).findByRole('button', { name: 'Other actions' }))
    await expect(await within(dialog).findByRole('button', { name: 'Download candidate for review' })).toBeVisible()
  },
}

/** Deleting the kept copy is offered only because an identical copy in the same folder takes its chapters. */
export const ReviewKeptDuplicateDeleteInstead: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(keptDuplicateDetail)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Keep the other copy and delete this one instead' }))
    await expect(await within(dialog).findByText(/Ch\. 77–85 then read from Ashen Crown Regression\/Ashen Crown Regression - Volume 10/)).toBeVisible()
  },
}

/** Opened from the spare: its only rival is its own twin, so the unlinked comparison is not repeated. */
export const ReviewSpareDuplicate: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(spareDuplicateDetail)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await expect(await within(dialog).findByText(/This file is byte for byte the same as the copy marked Keep/)).toBeVisible()
    await expect(within(dialog).queryByText('Not linked to any chapter')).not.toBeInTheDocument()
    await expect(await within(dialog).findByRole('button', { name: 'Delete this copy' })).toBeVisible()
  },
}

export const ReviewMissingArchive: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[3])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await expect(await within(dialog).findByText('File is gone from disk')).toBeVisible()
    await expect(await within(dialog).findByRole('button', { name: 'Download it again' })).toBeVisible()
    await expect(within(dialog).queryByRole('button', { name: 'Relink to this file' })).toBeNull()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Other actions' }))
    await waitFor(async () => expect(await within(dialog).findByRole('button', { name: 'Review record removal' })).toBeVisible())
  },
}

/** Clearing says what happens to the chapters before it does it, without a second dialog. */
export const ReviewMissingClearRecord: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[3])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Clear the record' }))
    await expect(await within(dialog).findByText(/still wanted, so automation may download them again/)).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Clear the record' }))
    await waitFor(() => expect(body.queryByRole('dialog', { name: 'Review archive' })).toBeNull())
  },
}

/** The folder was renamed: the same chapter sits at a new path, and one click points the record there. */
export const ReviewMissingFoundElsewhere: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(missingMovedDetail)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[3])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await expect(await within(dialog).findByText('Same pages; only the metadata changed')).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Relink to this file' }))
    await waitFor(() => expect(body.queryByRole('dialog', { name: 'Review archive' })).toBeNull())
  },
}

/** A match on the name alone asks for a look before it relinks. */
export const ReviewMissingNameOnly: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(missingWeakDetail)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[3])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await expect(await within(dialog).findByText('Possible match')).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Relink to this file' }))
    await expect(await within(dialog).findByText(/Only the name matches, so open the file/)).toBeVisible()
    await expect(await within(dialog).findByRole('button', { name: 'Relink' })).toBeVisible()
  },
}

/** Indexed only: the panel says it reads the file in full before downloading anything. */
export const ReviewDamagedIndexedOnly: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(damagedIndexedDetail)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await expect(await within(dialog).findByText(/has only been indexed. It is read in full first/)).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Get a new copy' }))
    await expect(await body.findByRole('dialog', { name: 'Review operation' })).toBeVisible()
  },
}

/** Changed outside Maki: the fix is the record, not the file. */
export const ReviewSizeMismatch: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(sizeMismatchDetail)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await expect(await within(dialog).findByText('File changed outside Maki')).toBeVisible()
    await expect(within(dialog).queryByRole('button', { name: 'Download candidate for review' })).toBeNull()
    const update = await within(dialog).findByRole('button', { name: 'Update the record' })
    await userEvent.click(update)
    await waitFor(() => expect(update).toBeEnabled())
  },
}

export const ReviewUnreadable: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(unreadableDetail)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await expect(await within(dialog).findByText('Maki cannot open this file')).toBeVisible()
    await expect(await within(dialog).findByText(/the drive or share it is on is mounted/)).toBeVisible()
    await waitFor(async () => expect((await within(dialog).findAllByRole('button', { name: 'Rescan' }))[0]).toBeVisible())
  },
}

export const ReviewUnlinkedWithoutSeries: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(unlinkedNoSeries)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[1])
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText(/not inside any series folder in its root/)).toBeVisible())
  },
}

export const ReviewUnrecognizedName: Story = {
  parameters: { ...tab('files'), msw: [fileDetail(unlinkedUnrecognized)] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[1])
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText(/carries no chapter or volume number/)).toBeVisible())
  },
}

export const ReviewLoadFailed: Story = {
  parameters: { ...tab('files'), msw: [http.get(api('/health/files/:id'), () => failWith(404, 'That archive is no longer in the inventory'))] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText('That archive is no longer in the inventory')).toBeVisible())
  },
}

export const RepairsTab: Story = {
  parameters: tab('repairs'),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Lanterns Over Kasumi')).toBeVisible()
    await expect(await canvas.findByText('The Ninth Kitchen')).toBeVisible()
    await expect(await canvas.findByText('No source had a candidate for chapter 4')).toBeVisible()
  },
}

/** A hundred deletions that failed the same way, as a bulk delete leaves them: one group, however many pages of rows. */
export const RepairsManyFailures: Story = {
  parameters: {
    ...tab('repairs'),
    msw: [
      http.get(api('/health/operations'), () => HttpResponse.json(groupOperations(manyFailedDeletions))),
      http.get(api('/health/operations/group'), ({ request }) => {
        const url = new URL(request.url)
        return HttpResponse.json(
          groupRows(manyFailedDeletions, url.searchParams.get('key') ?? '', Number(url.searchParams.get('take') ?? 30)),
        )
      }),
    ],
  },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('100 deletions')).toBeVisible()
    await expect(await canvas.findByText('2 replacements')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Review' })).toHaveLength(2))
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Details' })).toHaveLength(3))
    await expect(await canvas.findByRole('button', { name: 'Dismiss all' })).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Show all 100 operations' }))
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Details' })).toHaveLength(100))
  },
}

export const RepairsEmpty: Story = {
  parameters: { ...tab('repairs'), msw: [http.get(api('/health/operations'), () => HttpResponse.json(emptyList))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No repairs or deletions requested.')).toBeVisible()
  },
}

export const ReviewReplacement: Story = {
  parameters: tab('repairs'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' })).at(-1)!)
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review operation' })
    await expect(await within(dialog).findByText('Operation')).toBeVisible()
    const apply = await within(dialog).findByRole('button', { name: 'Apply replacement' })
    await waitFor(() => expect(apply).toBeDisabled())
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: 'I approve applying these replacement files.' }))
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: /Reset bookmarks and resume positions/ }))
    await waitFor(() => expect(apply).toBeEnabled())
  },
}

export const ReviewDeletion: Story = {
  parameters: tab('repairs'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' })).at(-2)!)
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review operation' })
    await waitFor(async () => expect(await within(dialog).findByText(/already gone from disk/)).toBeVisible())
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Cancel operation' })).toBeVisible())
  },
}

/** A deletion whose chapters move to an identical copy says so, instead of warning they lose their file. */
export const ReviewDeletionWithRelink: Story = {
  parameters: {
    ...tab('repairs'),
    msw: [http.get(api('/health/operations/:id'), () => HttpResponse.json(relinkDeleteDetail))],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' })).at(-2)!)
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review operation' })
    await expect(await within(dialog).findByText(/Ch\. 77–85 then read from the identical copy at/)).toBeVisible()
    await expect(within(dialog).queryByText(/Wanted chapters may be downloaded again/)).not.toBeInTheDocument()
  },
}

export const HistoryTab: Story = {
  parameters: tab('history'),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Index scan 6 finished: 6200 files, 3 new findings')).toBeVisible()
    await expect(await canvas.findByText('Status change')).toBeVisible()
  },
}

export const HistoryEmpty: Story = {
  parameters: { ...tab('history'), msw: [http.get(api('/health/history'), () => HttpResponse.json(emptyList))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing in the history yet.')).toBeVisible()
  },
}

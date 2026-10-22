import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import type { FileDetail } from '../api/health'
import {
  errorChecks,
  groupedChecks,
  manyFailedDeletions,
  overview,
  runningScan,
  unlinkedNoSeries,
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

export const ReviewMissingArchive: Story = {
  parameters: tab('files'),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[3])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Review archive' })
    await waitFor(async () => expect(await within(dialog).findByRole('button', { name: 'Review record removal' })).toBeVisible())
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

/** Thirty deletions that failed the same way, as a bulk delete leaves them. */
export const RepairsManyFailures: Story = {
  parameters: {
    ...tab('repairs'),
    msw: [
      http.get(api('/health/operations'), () =>
        HttpResponse.json({ items: manyFailedDeletions, total: manyFailedDeletions.length }),
      ),
    ],
  },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('30 deletions')).toBeVisible()
    await expect(await canvas.findByText('2 replacements')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Review' })).toHaveLength(5))
    await userEvent.click(await canvas.findByRole('button', { name: 'Show all 30' }))
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Review' })).toHaveLength(32))
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
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Operation #1' })
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
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Review' }))[1])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Operation #2' })
    await waitFor(async () => expect(await within(dialog).findByText(/already gone from disk/)).toBeVisible())
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Cancel operation' })).toBeVisible())
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

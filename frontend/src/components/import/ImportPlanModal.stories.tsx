import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { importPlans, importPlansLinked, importPlansRestore } from '../../../.storybook/mocks/operations/import'
import { ImportPlanModal } from './ImportPlanModal'

const meta = {
  component: ImportPlanModal,
  tags: ['ai-generated'],
  args: {
    rootFolderId: 1,
    items: [
      { folderName: 'Starlit Ferry (2023) [Digital]', metadataProviderId: 'mb-30011' },
      { folderName: 'Moth Orchard', metadataProviderId: 'mb-30012' },
    ],
    onClose: fn(),
  },
} satisfies Meta<typeof ImportPlanModal>

export default meta
type Story = StoryObj<typeof meta>

export const MixedPlan: Story = {
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Import preview' })
    await waitFor(async () => expect(await within(dialog).findByText('3 CBZs built')).toBeVisible())
    await waitFor(() => expect(within(dialog).getByText('The folder holds no readable comic files.')).toBeVisible())
    await waitFor(() => expect(within(dialog).getByText(/read off the file names until sources are found/)).toBeVisible())
    await waitFor(() => expect(within(dialog).getByText('2 files are left out')).toBeVisible())
  },
}

export const ExistingSeriesMerge: Story = {
  args: { items: [{ folderName: 'clockwork tea house', metadataProviderId: '10007' }] },
  parameters: {
    msw: [
      http.post(api('/libraryimport/plan'), async ({ request }) => {
        const body = (await request.json()) as { items: { folderName: string; metadataProviderId: string }[] }
        return HttpResponse.json(importPlansLinked(body.items))
      }),
    ],
  },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText(/Imported into Clockwork Tea House/)).toBeVisible())
    await waitFor(() => expect(body.getByText('Ch.2-14')).toBeVisible())
  },
}

export const ArchivedSeriesRestore: Story = {
  args: { items: [{ folderName: 'Lantern Keepers', metadataProviderId: '10009' }] },
  parameters: {
    msw: [
      http.post(api('/libraryimport/plan'), async ({ request }) => {
        const body = (await request.json()) as { items: { folderName: string; metadataProviderId: string }[] }
        return HttpResponse.json(importPlansRestore(body.items))
      }),
    ],
  },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText('Restore Lantern Keepers')).toBeVisible())
    await waitFor(() => expect(body.queryByText('In library')).not.toBeInTheDocument())
  },
}

export const PlanFailed: Story = {
  parameters: { msw: [http.post(api('/libraryimport/plan'), () => failWith(500, 'The root folder is not readable'))] },
  play: async ({ canvasElement }) => {
    await waitFor(async () => expect(await within(canvasElement.ownerDocument.body).findByText('The root folder is not readable')).toBeVisible())
  },
}

export const Loading: Story = {
  parameters: { msw: [http.post(api('/libraryimport/plan'), () => neverResolve())] },
  play: async ({ canvasElement }) => {
    await waitFor(async () => expect(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Import preview' })).toBeVisible())
  },
}

/** A first import of a whole library: the summary and the filtered list, files only once a folder opens. */
export const LargeImport: Story = {
  args: {
    items: Array.from({ length: 30 }, (_, n) => ({
      folderName: n === 1 ? 'Moth Orchard' : `Starlit Ferry ${String(n + 1).padStart(2, '0')}`,
      metadataProviderId: `mb-${30100 + n}`,
    })),
  },
  parameters: {
    msw: [
      http.post(api('/libraryimport/plan'), async ({ request }) => {
        const body = (await request.json()) as { items: { folderName: string; metadataProviderId: string }[] }
        // The first folder has odd files and the second fails; the rest only register one file each.
        const plans = importPlans(body.items).map((p, i) =>
          i < 2
            ? p
            : {
                ...p,
                folderAction: 'keep' as const,
                files: (p.files ?? []).slice(0, 1).map((f) => ({ ...f, name: `${p.folderName} - Ch.1.cbz` })),
                skipped: null,
              },
        )
        return HttpResponse.json(plans)
      }),
    ],
  },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Import preview' })
    const scope = within(dialog)
    await waitFor(async () => expect(await scope.findByText('Worth a look (2)')).toBeVisible())
    await waitFor(() => expect(scope.getByText('The folder holds no readable comic files.')).toBeVisible())
    expect(scope.queryByText('Starlit Ferry 05')).toBeNull()
    await userEvent.click(scope.getByText('All (30)'))
    await waitFor(() => expect(scope.getByText('Starlit Ferry 05')).toBeVisible())
    expect(scope.queryByText('Starlit Ferry 05 - Ch.1.cbz')).toBeNull()
    await userEvent.click(scope.getByRole('button', { name: /Starlit Ferry 05/ }))
    await waitFor(() => expect(scope.getByText('Starlit Ferry 05 - Ch.1.cbz')).toBeVisible())
    await userEvent.type(scope.getByRole('textbox', { name: 'Find a folder' }), 'moth')
    await waitFor(() => expect(scope.queryByText('Starlit Ferry 05')).toBeNull())
    await waitFor(() => expect(scope.getByText('Moth Orchard')).toBeVisible())
  },
}

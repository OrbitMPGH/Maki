import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { importPlansLinked, importPlansRestore } from '../../../.storybook/mocks/operations/import'
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
    await waitFor(async () => expect(await within(dialog).findByText('Registers 5 files, 3 of them CBZs Maki builds')).toBeVisible())
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
    await waitFor(() => expect(body.getByText('links Ch. 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 +1 more')).toBeVisible())
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

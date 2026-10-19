import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { renamePlan, seriesMsw } from '../../.storybook/mocks/series'
import { RenameSeriesModal } from './RenameSeriesModal'

const previewing = (plan: ReturnType<typeof renamePlan>) =>
  seriesMsw(http.get(api('/series/:id/rename/preview'), () => HttpResponse.json(plan)))

const meta = {
  component: RenameSeriesModal,
  tags: ['ai-generated'],
  args: { seriesId: 1, opened: true, onClose: fn() },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof RenameSeriesModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogOf = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Rename files' })

export const WithChanges: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await waitFor(() => expect(within(dialog).getByRole('button', { name: 'Rename' })).toBeEnabled())
  },
}

export const RenameApplies: Story = {
  args: { onClose: fn() },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    const rename = within(dialog).getByRole('button', { name: 'Rename' })
    await waitFor(() => expect(rename).toBeEnabled())
    await userEvent.click(rename)
  },
}

export const AlreadyMatches: Story = {
  parameters: { msw: previewing(renamePlan(1, { hasChanges: false, folderChanged: false, files: [] })) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText(/already match the current naming formats/)).toBeVisible()
  },
}

export const Conflicts: Story = {
  parameters: {
    msw: previewing(renamePlan(1, { conflicts: ['Lanterns Over Kasumi (2021) - c004.cbz', 'Lanterns Over Kasumi (2021) - c008.cbz'] })),
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText('Two chapters want one name')).toBeVisible()
    await expect(within(dialog).getByRole('button', { name: 'Rename' })).toBeDisabled()
  },
}

export const FilesOnly: Story = {
  parameters: { msw: previewing(renamePlan(1, { folderChanged: false })) },
}

export const Loading: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/rename/preview'), () => neverResolve())) },
}

export const PreviewFails: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/rename/preview'), () => failWith(500, 'The series folder is missing'))) },
}

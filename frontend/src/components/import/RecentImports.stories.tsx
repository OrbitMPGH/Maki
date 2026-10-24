import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { RecentImports, UndoBatchButton } from './RecentImports'

const meta = {
  component: RecentImports,
  tags: ['ai-generated'],
  args: { rootFolderId: 1, onUndone: fn() },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof RecentImports>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Recent imports')).toBeVisible()
    await expect(await canvas.findByText(/but not every file could be put back/)).toBeVisible()
    await expect(await canvas.findByText('Still finding sources. Undoing cancels that.')).toBeVisible()
    await expect(await canvas.findByText('The series was removed since, nothing to undo.')).toBeVisible()
  },
}

export const UndoOneFolder: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Undo' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Undo the import of Starlit Ferry (2023) [Digital]?' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Undo import' }))
    await waitFor(() => expect(args.onUndone).toHaveBeenCalled())
    await waitFor(async () => expect(await body.findByText('Undid the import of 1 folder')).toBeVisible())
  },
}

export const UndoWholeBatch: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Undo all' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Undo import' }))
    await waitFor(async () => expect(await body.findByText('The series has been read since, so it cannot be undone.')).toBeVisible())
  },
}

export const NoRecentImports: Story = {
  parameters: { msw: [http.get(api('/libraryimport/batches'), () => HttpResponse.json([]))] },
}

export const UndoThisImportButton: Story = {
  render: () => <UndoBatchButton batchId="batch-a" count={2} onUndone={fn()} />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Undo this import' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Undo the import of 2 folders?' })
    await waitFor(() => expect(dialog).toBeVisible())
  },
}

import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../../.storybook/mocks/api'
import { archivePreview } from '../../../.storybook/mocks/series'
import { ArchiveSeriesDialog } from './ArchiveSeriesDialog'

const retention = http.get(api('/recyclebin/retention'), () => HttpResponse.json({ days: 14 }))

const meta = {
  component: ArchiveSeriesDialog,
  tags: ['ai-generated'],
  args: {
    opened: true,
    onClose: fn(),
    onConfirm: fn(),
    onRemoveFilesChange: fn(),
    removeFiles: false,
    title: 'Lanterns Over Kasumi',
    seriesId: 1,
  },
  parameters: { msw: [retention, archivePreview()] },
} satisfies Meta<typeof ArchiveSeriesDialog>

export default meta
type Story = StoryObj<typeof meta>

export const FilesKept: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByRole('checkbox', { name: /also remove the files to free/i })).not.toBeChecked()
    await expect(within(dialog).queryByText(/recycle bin/)).toBeNull()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Archive' }))
    await waitFor(() => expect(args.onConfirm).toHaveBeenCalledOnce())
  },
}

export const FilesRemoved: Story = {
  args: { removeFiles: true },
  play: async ({ canvasElement }) => {
    await expect(await within(canvasElement.ownerDocument.body).findByText(/recycle bin for 14 days/)).toBeVisible()
  },
}

export const TogglesFiles: Story = {
  render: function Render(args) {
    const [removeFiles, setRemoveFiles] = useState(false)
    return <ArchiveSeriesDialog {...args} removeFiles={removeFiles} onRemoveFilesChange={setRemoveFiles} />
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: /also remove the files/i }))
    await expect(await within(dialog).findByText(/recycle bin/)).toBeVisible()
  },
}

export const OtherReadersWarning: Story = {
  parameters: { msw: [retention, archivePreview({ otherActiveReaders: 2 })] },
  play: async ({ canvasElement }) => {
    await expect(
      await within(canvasElement.ownerDocument.body).findByText(/2 other readers are partway through this series/),
    ).toBeVisible()
  },
}

export const NoFilesToFree: Story = {
  parameters: { msw: [retention, archivePreview({ freeableBytes: 0 })] },
  play: async ({ canvasElement }) => {
    await expect(
      await within(canvasElement.ownerDocument.body).findByRole('checkbox', { name: 'Also remove the files' }),
    ).toBeVisible()
  },
}

/** The size is fetched when the dialog opens; until it arrives the box reads without it. */
export const SizeLoading: Story = {
  parameters: { msw: [retention, http.get(api('/series/:id/archive-preview'), () => neverResolve())] },
  play: async ({ canvasElement }) => {
    await expect(
      await within(canvasElement.ownerDocument.body).findByRole('checkbox', { name: 'Also remove the files' }),
    ).toBeVisible()
  },
}

/** Several series at once: no size and no other-readers warning, so nothing is fetched. */
export const Bulk: Story = {
  args: { seriesId: null, title: '3 series' },
  parameters: { msw: [retention] },
  play: async ({ canvasElement }) => {
    await expect(
      await within(canvasElement.ownerDocument.body).findByRole('checkbox', { name: 'Also remove the files' }),
    ).toBeVisible()
  },
}

export const WithoutDeletePermission: Story = {
  args: { canRemoveFiles: false, removeFiles: true },
  play: async ({ canvasElement }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(within(dialog).getByRole('checkbox', { name: /also remove the files/i })).toBeDisabled())
  },
}

export const PermanentFallback: Story = {
  args: { removeFiles: true, onConfirmPermanent: fn() },
  play: async ({ canvasElement }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByText(/recycle bin for 14 days/)).toBeVisible()
  },
}

export const Archiving: Story = {
  args: { loading: true },
}

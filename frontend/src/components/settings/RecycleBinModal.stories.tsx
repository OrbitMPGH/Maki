import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, daysAgo, daysAhead } from '../../../.storybook/mocks/api'
import { recycleBin } from '../../../.storybook/mocks/settings/data'
import type { RecycleBin, RecycleBinEntry } from '../../api/recycleBin'
import { RecycleBinModal } from './RecycleBinModal'

const MB = 1024 ** 2

// A whole series removed an hour ago: the case that made the settings card a wall of rows.
const removedSeries: RecycleBinEntry[] = Array.from({ length: 24 }, (_, i) => ({
  id: 100 + i,
  seriesId: 2,
  seriesTitle: 'The Ninth Kitchen',
  seriesExists: true,
  fileName: `The Ninth Kitchen - c${String(i + 1).padStart(3, '0')}.cbz`,
  relativePath: `The Ninth Kitchen (2019)/The Ninth Kitchen - c${String(i + 1).padStart(3, '0')}.cbz`,
  size: 22 * MB,
  reason: 'seriesDelete',
  deletedAt: daysAgo(0.05),
  deletedBy: 'Maki',
  expiresAt: daysAhead(14),
  missing: false,
  chapters: [{ number: i + 1, volume: Math.floor(i / 8) + 1, language: 'en' }],
}))

const fullBin: RecycleBin = {
  ...recycleBin,
  totalBytes: recycleBin.totalBytes + removedSeries.length * 22 * MB,
  entries: [...recycleBin.entries, ...removedSeries],
}

const withBin = (bin: RecycleBin) => settingsMsw(http.get(api('/recyclebin'), () => HttpResponse.json(bin)))

const meta = {
  component: RecycleBinModal,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn() },
  parameters: { msw: withBin(fullBin) },
} satisfies Meta<typeof RecycleBinModal>

export default meta
type Story = StoryObj<typeof meta>

const modalOf = async (canvasElement: HTMLElement) =>
  within(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Recycle bin' }))

const groupOf = async (modal: ReturnType<typeof within>, title: string) =>
  within(await modal.findByRole('region', { name: title }))

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const modal = await modalOf(canvasElement)
    await expect(await modal.findByText('Lanterns Over Kasumi - c131.cbz')).toBeVisible()
    await expect(await modal.findByText('No longer in the library')).toBeVisible()
    await expect(await modal.findByText('The file is missing from the bin folder.')).toBeVisible()
    await expect(await modal.findByText('27 files, 840 MB')).toBeVisible()
    const kitchen = await groupOf(modal, 'The Ninth Kitchen')
    await waitFor(() => expect(kitchen.getByRole('button', { expanded: false })).toBeVisible())
    // Collapse keeps a folded group's rows mounted, just hidden.
    await waitFor(() => expect(kitchen.getByText('The Ninth Kitchen - c001.cbz')).not.toBeVisible())
  },
}

export const ExpandGroup: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const kitchen = await groupOf(await modalOf(canvasElement), 'The Ninth Kitchen')
    await userEvent.click(await kitchen.findByRole('button', { expanded: false }))
    await expect(await kitchen.findByText('The Ninth Kitchen - c001.cbz')).toBeVisible()
    await waitFor(() => expect(kitchen.getAllByRole('button', { name: 'Restore' })).toHaveLength(24))
  },
}

export const FilterBySearch: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const modal = await modalOf(canvasElement)
    await modal.findByText('Lanterns Over Kasumi - c131.cbz')
    await userEvent.type(await modal.findByRole('textbox', { name: 'Filter by series or file' }), 'petal')
    await expect(await modal.findByText('Iron Petal Academy - c12.cbz')).toBeVisible()
    await waitFor(() => expect(modal.queryByText('Lanterns Over Kasumi - c131.cbz')).toBeNull())
    await expect(await modal.findByText('Showing 1 of 27 files')).toBeVisible()
  },
}

export const FilterByReason: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const modal = await modalOf(canvasElement)
    await modal.findByText('Lanterns Over Kasumi - c131.cbz')
    await userEvent.click(await modal.findByRole('tab', { name: /Removed series/ }))
    await waitFor(() => expect(modal.queryByText('Lanterns Over Kasumi - c131.cbz')).toBeNull())
    await expect(await modal.findByRole('region', { name: 'The Ninth Kitchen' })).toBeVisible()
    await expect(await modal.findByText('Cloudless Archipelago (whole series).cbz')).toBeVisible()
  },
}

export const RestoreAll: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const kitchen = await groupOf(await modalOf(canvasElement), 'The Ninth Kitchen')
    await userEvent.click(await kitchen.findByRole('button', { name: 'Restore all' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText('Restored 24 files')).toBeVisible()
  },
}

export const DeleteAllConfirm: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const kitchen = await groupOf(await modalOf(canvasElement), 'The Ninth Kitchen')
    await userEvent.click(await kitchen.findByRole('button', { name: 'Delete all permanently' }))
    const dialog = within(
      await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Delete 24 files permanently?' }),
    )
    await expect(await dialog.findByText('Every file from The Ninth Kitchen in the bin.')).toBeVisible()
  },
}

export const EmptyConfirm: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const modal = await modalOf(canvasElement)
    const empty = await modal.findByRole('button', { name: 'Empty bin' })
    await waitFor(() => expect(empty).toBeEnabled())
    await userEvent.click(empty)
    const dialog = within(
      await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Empty the recycle bin?' }),
    )
    await expect(await dialog.findByText('Deletes 27 files for good.')).toBeVisible()
  },
}

export const Empty: Story = {
  parameters: { msw: withBin({ ...recycleBin, entries: [], totalBytes: 0 }) },
  play: async ({ canvasElement }) => {
    const modal = await modalOf(canvasElement)
    await expect(await modal.findByText('The recycle bin is empty')).toBeVisible()
    await waitFor(() => expect(modal.getByRole('button', { name: 'Empty bin' })).toBeDisabled())
  },
}

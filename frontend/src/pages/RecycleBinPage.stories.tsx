import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../.storybook/mocks/settings'
import { api, daysAgo, daysAhead } from '../../.storybook/mocks/api'
import { recycleBin } from '../../.storybook/mocks/settings/data'
import type { RecycleBin, RecycleBinEntry } from '../api/recycleBin'
import RecycleBinPage from './RecycleBinPage'

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
  component: RecycleBinPage,
  tags: ['ai-generated'],
  parameters: {
    layout: 'fullscreen',
    router: { path: '/library/recycle-bin', initialEntries: ['/library/recycle-bin'] },
    msw: withBin(fullBin),
  },
} satisfies Meta<typeof RecycleBinPage>

export default meta
type Story = StoryObj<typeof meta>

const groupOf = async (canvas: ReturnType<typeof within>, title: string) =>
  within(await canvas.findByRole('region', { name: title }))

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Lanterns Over Kasumi - c131.cbz')).toBeVisible()
    await expect(await canvas.findByText('No longer in the library')).toBeVisible()
    await expect(await canvas.findByText('The file is missing from the bin folder.')).toBeVisible()
    const kitchen = await groupOf(canvas, 'The Ninth Kitchen')
    await waitFor(() => expect(kitchen.getByRole('button', { expanded: false })).toBeVisible())
    await expect(kitchen.queryByText('The Ninth Kitchen - c001.cbz')).toBeNull()
  },
}

export const ExpandGroup: Story = {
  play: async ({ canvas, userEvent }) => {
    const kitchen = await groupOf(canvas, 'The Ninth Kitchen')
    await userEvent.click(await kitchen.findByRole('button', { expanded: false }))
    await expect(await kitchen.findByText('The Ninth Kitchen - c001.cbz')).toBeVisible()
    await waitFor(() => expect(kitchen.getAllByRole('button', { name: 'Restore' })).toHaveLength(24))
  },
}

export const FilterBySearch: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('Lanterns Over Kasumi - c131.cbz')
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Filter by series or file' }), 'petal')
    await expect(await canvas.findByText('Iron Petal Academy - c12.cbz')).toBeVisible()
    await waitFor(() => expect(canvas.queryByText('Lanterns Over Kasumi - c131.cbz')).toBeNull())
    await expect(await canvas.findByText('Showing 1 file.')).toBeVisible()
  },
}

export const FilterByReason: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('Lanterns Over Kasumi - c131.cbz')
    await userEvent.click(await canvas.findByRole('tab', { name: /Removed series/ }))
    await waitFor(() => expect(canvas.queryByText('Lanterns Over Kasumi - c131.cbz')).toBeNull())
    await expect(await canvas.findByRole('region', { name: 'The Ninth Kitchen' })).toBeVisible()
    await expect(await canvas.findByText('Cloudless Archipelago (whole series).cbz')).toBeVisible()
  },
}

export const RestoreAll: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const kitchen = await groupOf(canvas, 'The Ninth Kitchen')
    await userEvent.click(await kitchen.findByRole('button', { name: 'Restore all' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText('Restored 24 files')).toBeVisible()
  },
}

export const DeleteAllConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const kitchen = await groupOf(canvas, 'The Ninth Kitchen')
    await userEvent.click(await kitchen.findByRole('button', { name: 'Delete all permanently' }))
    const dialog = within(await within(canvasElement.ownerDocument.body).findByRole('dialog'))
    await expect(await dialog.findByText('Delete 24 files permanently?')).toBeVisible()
    await expect(await dialog.findByText('Every file from The Ninth Kitchen in the bin.')).toBeVisible()
  },
}

export const EmptyConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const empty = await canvas.findByRole('button', { name: 'Empty bin' })
    await waitFor(() => expect(empty).toBeEnabled())
    await userEvent.click(empty)
    const dialog = within(await within(canvasElement.ownerDocument.body).findByRole('dialog'))
    await expect(await dialog.findByText('Deletes 27 files for good.')).toBeVisible()
  },
}

export const Empty: Story = {
  parameters: { msw: withBin({ ...recycleBin, entries: [], totalBytes: 0 }) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The recycle bin is empty')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Empty bin' })).toBeDisabled())
  },
}

export const NonAdmin: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Lanterns Over Kasumi - c131.cbz')).toBeVisible()
    await expect(canvas.queryByRole('link', { name: 'Recycle bin settings' })).toBeNull()
  },
}

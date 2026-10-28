import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { seriesConversionRestorable } from '../../../.storybook/mocks/series'
import type { UndoConversionRequest } from '../../api/pageConversion'
import { UndoConversionModal } from './UndoConversionModal'

const meta = {
  component: UndoConversionModal,
  tags: ['ai-generated'],
  args: {
    opened: true,
    onClose: fn(),
    seriesId: 1,
    history: seriesConversionRestorable,
  },
} satisfies Meta<typeof UndoConversionModal>

export default meta
type Story = StoryObj<typeof meta>

const body = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

let sent: UndoConversionRequest | null = null

const recordUndo = (answer: object = { restored: 48, skipped: 0, reasons: [] }) =>
  http.post(api('/series/:id/pageconversion/undo'), async ({ request }) => {
    sent = (await request.json()) as UndoConversionRequest
    return HttpResponse.json(answer)
  })

const dialogOf = async (canvasElement: HTMLElement) => within(await body(canvasElement).findByRole('dialog', { name: 'Undo conversion' }))

export const RestoreOriginals: Story = {
  parameters: { msw: [recordUndo()] },
  beforeEach: () => {
    sent = null
  },
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(dialog.getByText('48 files have their original in the trash (2.5 GB).')).toBeVisible()
    await expect(dialog.getByText('The others were converted on download or their original has been purged.')).toBeVisible()
    await expect(dialog.getByText('Converted with WebP, High and AVIF, Balanced, PNG lossless.')).toBeVisible()
    await expect(dialog.getByRole('radio', { name: 'Restore the originals' })).toBeChecked()
    await expect(dialog.getByRole('checkbox', { name: 'Also set this series to Never' })).toBeEnabled()

    await userEvent.click(dialog.getByRole('button', { name: 'Undo' }))
    await expect(await body(canvasElement).findByText('Restored 48 files')).toBeVisible()
    await waitFor(() => expect(args.onClose).toHaveBeenCalledOnce())
    expect(sent).toEqual({ setNever: false, convertAgain: false })
  },
}

export const RestoreAndConvertAgain: Story = {
  parameters: {
    msw: [recordUndo({ restored: 48, skipped: 0, reasons: ['48 files are queued for conversion.'] })],
  },
  beforeEach: () => {
    sent = null
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(dialog.getByRole('checkbox', { name: 'Also set this series to Never' }))
    await userEvent.click(dialog.getByRole('radio', { name: 'Restore and convert again with the current settings' }))
    await expect(dialog.getByRole('checkbox', { name: 'Also set this series to Never' })).toBeDisabled()
    await expect(dialog.getByRole('checkbox', { name: 'Also set this series to Never' })).not.toBeChecked()

    await userEvent.click(dialog.getByRole('button', { name: 'Undo' }))
    await expect(await body(canvasElement).findByText('48 files are queued for conversion.')).toBeVisible()
    expect(sent).toEqual({ setNever: false, convertAgain: true })
  },
}

export const RestoreAndSetToNever: Story = {
  parameters: { msw: [recordUndo({ restored: 1, skipped: 0, reasons: [] })] },
  beforeEach: () => {
    sent = null
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(dialog.getByRole('checkbox', { name: 'Also set this series to Never' }))
    await expect(dialog.getByRole('checkbox', { name: 'Also set this series to Never' })).toBeChecked()
    await userEvent.click(dialog.getByRole('button', { name: 'Undo' }))
    await expect(await body(canvasElement).findByText('Restored 1 file')).toBeVisible()
    expect(sent).toEqual({ setNever: true, convertAgain: false })
  },
}

export const SomeFilesSkipped: Story = {
  parameters: {
    msw: [
      recordUndo({
        restored: 45,
        skipped: 3,
        reasons: ['3 files were skipped: their archive changed after it was converted.'],
      }),
    ],
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(dialog.getByRole('button', { name: 'Undo' }))
    await expect(await body(canvasElement).findByText('Restored 45 files')).toBeVisible()
    await expect(body(canvasElement).getByText('3 files were skipped: their archive changed after it was converted.')).toBeVisible()
  },
}

export const NothingRestored: Story = {
  parameters: {
    msw: [recordUndo({ restored: 0, skipped: 48, reasons: ['A download is in flight for this series.'] })],
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(dialog.getByRole('button', { name: 'Undo' }))
    await expect(await body(canvasElement).findByText('Nothing was restored')).toBeVisible()
    await expect(body(canvasElement).getByText('A download is in flight for this series.')).toBeVisible()
  },
}

export const SingleFile: Story = {
  args: {
    history: { ...seriesConversionRestorable, restorableFiles: 1, restorableBytes: 52_428_800, checkedWith: ['lossless'] },
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(dialog.getByText('1 file has its original in the trash (50 MB).')).toBeVisible()
    await expect(dialog.getByText('Converted with Lossless.')).toBeVisible()
  },
}

export const Cancel: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(dialog.getByRole('button', { name: 'Cancel' }))
    await expect(args.onClose).toHaveBeenCalledOnce()
  },
}

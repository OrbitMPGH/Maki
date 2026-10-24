import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { awaitingImportItem, guardedItem, importPlan } from '../../.storybook/mocks/operations/queue'
import { ImportReviewModal } from './ImportReviewModal'

const settled = fn()
const recordDecision = http.post(api('/queue/:id/import'), async ({ request }) => {
  settled(await request.json())
  return HttpResponse.json({ imported: 2, linked: 14, skipped: 1, deleted: 6 })
})

const meta = {
  component: ImportReviewModal,
  tags: ['ai-generated'],
  args: { queueItemId: awaitingImportItem.id, onClose: fn() },
  parameters: { msw: [recordDecision] },
  beforeEach: () => {
    settled.mockClear()
  },
} satisfies Meta<typeof ImportReviewModal>

export default meta
type Story = StoryObj<typeof meta>

const openDialog = async (canvasElement: HTMLElement) => {
  const body = within(canvasElement.ownerDocument.body)
  const element = await body.findByRole('dialog', { name: 'Review import' })
  return { element, dialog: within(element) }
}

export const Default: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const { element, dialog } = await openDialog(canvasElement)
    await dialog.findByRole('checkbox', { name: 'Import Clockwork Tea House v01.cbz' })
    await waitFor(() => expect(element).toHaveTextContent('12 replaced'))
    await waitFor(() => expect(element).toHaveTextContent('2 new'))
    await waitFor(() => expect(dialog.getByText(/6 files go to the trash/)).toBeVisible())
    await userEvent.click(dialog.getByRole('button', { name: 'Import 3 files' }))
    await waitFor(() => expect(settled).toHaveBeenCalledWith(expect.objectContaining({ mode: 'Replace' })))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const OnlyAddWhatIsMissing: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const { element, dialog } = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByText('Only add what is missing'))
    await waitFor(() => expect(dialog.getByText('nothing new')).toBeVisible())
    await waitFor(() => expect(element).toHaveTextContent('4 kept'))
    expect(dialog.queryByText(/go to the trash/)).toBeNull()
    await userEvent.click(dialog.getByRole('button', { name: 'Import 2 files' }))
    await waitFor(() => expect(settled).toHaveBeenCalledWith(expect.objectContaining({ mode: 'SkipExisting' })))
  },
}

export const LeaveAFileOut: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const { element, dialog } = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('checkbox', { name: 'Import Clockwork Tea House v02.cbz' }))
    await waitFor(() => expect(element).toHaveTextContent('4 replaced'))
    await waitFor(() => expect(dialog.getByText(/1 file goes to the trash/)).toBeVisible())
    await userEvent.click(dialog.getByRole('button', { name: 'Import 2 files' }))
    await waitFor(() =>
      expect(settled).toHaveBeenCalledWith(expect.objectContaining({ skipFiles: ['Clockwork Tea House v02.cbz'] })),
    )
  },
}

export const NothingTicked: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const { dialog } = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'None' }))
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Nothing to import' })).toBeDisabled())
  },
}

export const IgnoreDownload: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const { dialog } = await openDialog(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Ignore this download' }))
    await waitFor(() => expect(settled).toHaveBeenCalledWith(expect.objectContaining({ mode: 'Reject' })))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const UpgradeWithSkips: Story = {
  args: { queueItemId: guardedItem.id },
  play: async ({ canvasElement, userEvent }) => {
    const { dialog } = await openDialog(canvasElement)
    await waitFor(async () => expect(await dialog.findByText('Held back before importing')).toBeVisible())
    const atCutoff = await dialog.findByRole('checkbox', { name: 'Import Clockwork Tea House v02.cbz' })
    await waitFor(() => expect(atCutoff).not.toBeChecked())
    await waitFor(() => expect(dialog.getByText('At cutoff')).toBeVisible())
    await waitFor(() => expect(dialog.getByText(/1 file goes to the trash/)).toBeVisible())
    await userEvent.click(atCutoff)
    await waitFor(() => expect(dialog.getByText(/6 files go to the trash/)).toBeVisible())
  },
}

export const UnreadableDownload: Story = {
  parameters: {
    msw: [
      http.get(api('/queue/:id/import-plan'), ({ params }) =>
        HttpResponse.json(
          importPlan(Number(params.id), { error: 'The download folder is empty or no longer reachable.', files: [] }),
        ),
      ),
    ],
  },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText("Can't read this download")).toBeVisible())
    await waitFor(() => expect(body.getByRole('button', { name: 'Discard download' })).toBeVisible())
  },
}

export const LoadFailed: Story = {
  parameters: { msw: [http.get(api('/queue/:id/import-plan'), () => failWith(500, 'The plan could not be built'))] },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText("Can't load this import")).toBeVisible())
    await waitFor(() => expect(body.getByRole('button', { name: 'Retry' })).toBeVisible())
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/queue/:id/import-plan'), () => neverResolve())] },
  play: async ({ canvasElement }) => {
    await waitFor(async () => expect(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Review import' })).toBeVisible())
  },
}

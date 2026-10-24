import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import { libraryHandlers } from '../../.storybook/mocks/library'
import { TagManagerModal } from './TagManagerModal'

const meta = {
  component: TagManagerModal,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn() },
  parameters: { layout: 'fullscreen', msw: libraryHandlers },
} satisfies Meta<typeof TagManagerModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogIn = (canvasElement: HTMLElement) =>
  within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Manage tags' })

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogIn(canvasElement)
    await expect(await within(dialog).findByText('Favourites')).toBeVisible()
    await expect(await within(dialog).findByText('Waiting for volume')).toBeVisible()
    await expect(await within(dialog).findByText('0 series')).toBeVisible()
  },
}

export const Empty: Story = {
  parameters: { msw: [http.get(api('/tags'), () => HttpResponse.json([])), ...libraryHandlers] },
  play: async ({ canvasElement }) => {
    const dialog = await dialogIn(canvasElement)
    await expect(await within(dialog).findByText(/No tags yet\./)).toBeVisible()
  },
}

const onCreate = fn()

export const CreateTag: Story = {
  parameters: {
    msw: [
      http.post(api('/tags'), async ({ request }) => {
        const payload = (await request.json()) as { label: string }
        onCreate(payload)
        return HttpResponse.json({ id: 50, label: payload.label, color: 'blue', seriesCount: 0 })
      }),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogIn(canvasElement)
    const add = await within(dialog).findByRole('button', { name: 'Add' })
    await waitFor(() => expect(add).toBeDisabled())
    await userEvent.type(await within(dialog).findByRole('textbox', { name: 'New tag…' }), 'Read on the train')
    await userEvent.click(add)
    await waitFor(() => expect(onCreate).toHaveBeenCalledWith({ label: 'Read on the train' }))
  },
}

const onRename = fn()

export const RenameTag: Story = {
  parameters: {
    msw: [
      http.put(api('/tags/:id'), async ({ params, request }) => {
        onRename(params.id, await request.json())
        return HttpResponse.json({ id: 1, label: 'All-time favourites', color: 'red', seriesCount: 3 })
      }),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogIn(canvasElement)
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Rename Favourites' }))
    const input = await within(dialog).findByRole('textbox', { name: 'Rename Favourites' })
    await userEvent.clear(input)
    await userEvent.type(input, 'All-time favourites{Enter}')
    await waitFor(() => expect(onRename).toHaveBeenCalledWith('1', { label: 'All-time favourites' }))
  },
}

const onRecolour = fn()

export const ChangeColour: Story = {
  parameters: {
    msw: [
      http.put(api('/tags/:id'), async ({ params, request }) => {
        onRecolour(params.id, await request.json())
        return HttpResponse.json({ id: 3, label: 'Comfort reads', color: 'grape', seriesCount: 3 })
      }),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogIn(canvasElement)
    await userEvent.click(await within(dialog).findByText('Comfort reads'))
    const swatches = await within(canvasElement.ownerDocument.body).findAllByRole('button', { name: /^Colour / })
    await expect(swatches.length).toBeGreaterThan(5)
    await userEvent.click(swatches[1])
    await waitFor(() => expect(onRecolour).toHaveBeenCalledWith('3', { color: 'grape' }))
  },
}

const onDelete = fn()

export const DeleteTag: Story = {
  parameters: {
    msw: [
      http.delete(api('/tags/:id'), ({ params }) => {
        onDelete(params.id)
        return new HttpResponse(null, { status: 204 })
      }),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogIn(canvasElement)
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Delete Shared with Sam' }))
    const confirm = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Delete this tag?' })
    await expect(await within(confirm).findByText('It is removed from 2 series. This cannot be undone.')).toBeInTheDocument()
    await userEvent.click(await within(confirm).findByRole('button', { name: 'Delete tag' }))
    await waitFor(() => expect(onDelete).toHaveBeenCalledWith('5'))
  },
}

export const CreateFails: Story = {
  parameters: {
    msw: [http.post(api('/tags'), () => failWith(500, 'Could not save the tag')), ...libraryHandlers],
  },
  // The global error handler toasts the failure; the text stays in the box so the label is not lost.
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogIn(canvasElement)
    await userEvent.type(await within(dialog).findByRole('textbox', { name: 'New tag…' }), 'Doomed')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Add' }))
    await waitFor(() => expect(within(dialog).getByRole('textbox', { name: 'New tag…' })).toHaveValue('Doomed'))
  },
}

export const Closed: Story = {
  args: { opened: false },
}

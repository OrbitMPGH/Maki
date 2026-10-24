import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { RemoveSeriesDialog } from './RemoveSeriesDialog'

const meta = {
  component: RemoveSeriesDialog,
  tags: ['ai-generated'],
  args: {
    opened: true,
    onClose: fn(),
    onConfirm: fn(),
    onDeleteFilesChange: fn(),
    deleteFiles: false,
    title: 'Remove Lanterns Over Kasumi',
    children: 'This takes the series out of your library. 142 chapters stay on disk unless you tick the box below.',
  },
  parameters: { msw: [http.get(api('/recyclebin/retention'), () => HttpResponse.json({ days: 14 }))] },
} satisfies Meta<typeof RemoveSeriesDialog>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Remove' }))
    await expect(args.onConfirm).toHaveBeenCalledOnce()
  },
}

export const DeletingFiles: Story = {
  args: { deleteFiles: true },
  play: async ({ canvasElement }) => {
    await expect(await within(canvasElement.ownerDocument.body).findByText(/recycle bin for 14 days/)).toBeVisible()
  },
}

export const TogglesFiles: Story = {
  render: function Render(args) {
    const [deleteFiles, setDeleteFiles] = useState(false)
    return <RemoveSeriesDialog {...args} deleteFiles={deleteFiles} onDeleteFilesChange={setDeleteFiles} />
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: /also delete files/i }))
    await expect(await within(dialog).findByText(/recycle bin/)).toBeVisible()
  },
}

export const BinUnavailable: Story = {
  args: {
    deleteFiles: true,
    onConfirmPermanent: fn(),
  },
}

export const Removing: Story = {
  args: { loading: true },
}

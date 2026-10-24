import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { IgnoredFoldersModal } from './IgnoredFoldersModal'

const meta = {
  component: IgnoredFoldersModal,
  tags: ['ai-generated'],
  args: { rootFolderId: 1, onClose: fn() },
} satisfies Meta<typeof IgnoredFoldersModal>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Ignored folders' })
    await waitFor(async () => expect(await within(dialog).findByText('Extras and bonus pages')).toBeVisible())
    await userEvent.click((await within(dialog).findAllByRole('button', { name: 'Stop ignoring' }))[0])
    await waitFor(async () => expect(await within(dialog).findByText('Scan again to list the folders you brought back.')).toBeVisible())
  },
}

export const Empty: Story = {
  parameters: { msw: [http.get(api('/libraryimport/ignored'), () => HttpResponse.json([]))] },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText('No folders are ignored in this root folder.')).toBeVisible())
  },
}

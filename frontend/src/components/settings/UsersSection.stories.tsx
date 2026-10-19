import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { users } from '../../../.storybook/mocks/settings/data'
import { UsersSection } from './UsersSection'

const meta = {
  component: UsersSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 980 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof UsersSection>

export default meta
type Story = StoryObj<typeof meta>

async function dialogOf(canvasElement: HTMLElement) {
  const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
  await waitFor(() => expect(dialog).toBeVisible())
  return within(dialog)
}

export const UserList: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Noa')).toBeVisible()
    await expect(await canvas.findByText('you')).toBeVisible()
    await expect(canvas.getByText('disabled')).toBeVisible()
    await expect(canvas.getByText('guest')).toBeVisible()
    // Your own row has no Delete: the server refuses it anyway.
    await expect(canvas.getAllByRole('button', { name: 'Delete' })).toHaveLength(users.length - 1)
  },
}

export const Loading: Story = {
  parameters: { msw: settingsMsw(http.get(api('/users'), () => neverResolve())) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Add user' })).toBeVisible()
  },
}

export const LoadFailed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/users'), () => failWith(500))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Add user' })).toBeVisible()
    await expect(canvas.queryByText('Noa')).toBeNull()
  },
}

export const AddUser: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Add user' }))
    const dialog = await dialogOf(canvasElement)
    await expect(dialog.getByRole('textbox', { name: /^Username/ })).toHaveValue('')
    await userEvent.type(dialog.getByRole('textbox', { name: /^Username/ }), 'sora')
    await userEvent.type(dialog.getByLabelText(/^Password/), 'a-long-enough-password')
    await userEvent.click(dialog.getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).toBeNull())
  },
}

export const AddUserRejected: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/users'), () => failWith(409, 'That username is taken.'))),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Add user' }))
    const dialog = await dialogOf(canvasElement)
    await userEvent.type(dialog.getByRole('textbox', { name: /^Username/ }), 'rin')
    await userEvent.type(dialog.getByLabelText(/^Password/), 'a-long-enough-password')
    await userEvent.click(dialog.getByRole('button', { name: 'Create' }))
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Create' })).toBeEnabled())
    await expect(within(canvasElement.ownerDocument.body).getByRole('dialog')).toBeVisible()
  },
}

export const EditYourself: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Noa')
    const [edit] = canvas.getAllByRole('button', { name: 'Edit' })
    await userEvent.click(edit)
    const dialog = await dialogOf(canvasElement)
    await expect(dialog.getByRole('switch', { name: /^Administrator/ })).toBeDisabled()
    await expect(dialog.getByRole('switch', { name: /^Disabled/ })).toBeDisabled()
  },
}

export const EditARestrictedReader: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Noa')
    const edit = within(canvas.getByText('Rin').closest('tr')!).getByRole('button', { name: 'Edit' })
    await userEvent.click(edit)
    const dialog = await dialogOf(canvasElement)
    await expect(dialog.getByRole('textbox', { name: /^Username/ })).toHaveValue('reader')
    await expect(dialog.getByRole('switch', { name: /^Access all root folders/ })).not.toBeChecked()
    await expect(dialog.getByText('/library/manga')).toBeVisible()
  },
}

export const EditUserWithTwoFactorAndSso: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/users'), () =>
        HttpResponse.json(users.map((u) => (u.id === 3 ? { ...u, twoFactorEnabled: true, oidcLinked: true } : u))),
      ),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Noa')
    await userEvent.click(within(canvas.getByText('Noa').closest('tr')!).getByRole('button', { name: 'Edit' }))
    const dialog = await dialogOf(canvasElement)
    await expect(dialog.getByRole('button', { name: 'Reset two-factor' })).toBeVisible()
    await userEvent.click(dialog.getByRole('button', { name: 'Remove single sign-on' }))
    const confirm = await within(canvasElement.ownerDocument.body).findByText(/Remove single sign-on for/)
    await expect(confirm).toBeVisible()
  },
}

export const DeleteConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Noa')
    await userEvent.click(within(canvas.getByText('Noa').closest('tr')!).getByRole('button', { name: 'Delete' }))
    const dialog = await dialogOf(canvasElement)
    await expect(dialog.getByText('Delete Noa?')).toBeVisible()
    await expect(dialog.getByText("They will no longer be able to sign in. This can't be undone.")).toBeVisible()
    await userEvent.click(dialog.getByRole('button', { name: 'Delete user' }))
    await waitFor(() => expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).toBeNull())
  },
}

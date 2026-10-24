import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../.storybook/mocks/settings'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { NotificationsSection } from './NotificationsSection'

const meta = {
  component: NotificationsSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 980 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof NotificationsSection>

export default meta
type Story = StoryObj<typeof meta>

async function dialogOf(canvasElement: HTMLElement) {
  const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
  await waitFor(() => expect(dialog).toBeVisible())
  return within(dialog)
}

export const Connections: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Reading room Discord')).toBeVisible()
    await expect(await canvas.findByText('Phone alerts')).toBeVisible()
    await expect(await canvas.findByText('Home Assistant')).toBeVisible()
    await expect(await canvas.findByText('Healthy')).toBeVisible()
    await expect(await canvas.findByText('Not used yet')).toBeVisible()
    await expect(await canvas.findByText('Disabled')).toBeVisible()
  },
}

export const FailingConnectionShowsItsError: Story = {
  play: async ({ canvas }) => {
    await canvas.findByText('Phone alerts')
    const row = (await canvas.findByText('Phone alerts')).closest('tr')!
    await expect(await within(row).findByText(/Failing: 4 misses in a row/)).toBeVisible()
    await expect(await within(row).findByText('Favourites')).toBeVisible()
  },
}

export const Empty: Story = {
  parameters: { msw: settingsMsw(http.get(api('/notifications'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No notification connections yet.')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: settingsMsw(http.get(api('/notifications'), () => neverResolve())) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Add connection' })).toBeVisible()
  },
}

export const LoadFailed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/notifications'), () => failWith(500))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Couldn't load notification connections")).toBeVisible()
  },
}

export const EditConnection: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Reading room Discord')
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Edit connection' }))[0])
    const dialog = await dialogOf(canvasElement)
    await waitFor(() => expect(dialog.getByRole('textbox', { name: 'Name' })).toHaveValue('Reading room Discord'))
    await waitFor(() => expect(dialog.getByRole('switch', { name: /^Chapter downloaded/ })).toBeChecked())
    await waitFor(() => expect(dialog.getByRole('switch', { name: /^Series added/ })).not.toBeChecked())
  },
}

export const AddConnectionNeedsRequiredFields: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Reading room Discord')
    await userEvent.click(await canvas.findByRole('button', { name: 'Add connection' }))
    const dialog = await dialogOf(canvasElement)
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Save' })).toBeDisabled())
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Test' })).toBeDisabled())
    await userEvent.type(await dialog.findByRole('textbox', { name: 'Name' }), 'Weekend digest')
    await userEvent.type(await dialog.findByRole('textbox', { name: 'Webhook URL' }), 'https://discord.com/api/webhooks/1/abc')
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Save' })).toBeEnabled())
  },
}

export const AddConnectionTestAndSave: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Reading room Discord')
    await userEvent.click(await canvas.findByRole('button', { name: 'Add connection' }))
    const dialog = await dialogOf(canvasElement)
    await userEvent.type(await dialog.findByRole('textbox', { name: 'Name' }), 'Weekend digest')
    await userEvent.type(await dialog.findByRole('textbox', { name: 'Webhook URL' }), 'https://discord.com/api/webhooks/1/abc')
    await userEvent.click(await dialog.findByRole('button', { name: 'Test' }))
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Test' })).toBeEnabled())
    await userEvent.click(await dialog.findByRole('button', { name: 'Save' }))
    await waitFor(() => expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).toBeNull())
  },
}

export const TestFails: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/notifications/test'), () => HttpResponse.json({ success: false }))),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Reading room Discord')
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Edit connection' }))[0])
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Test' }))
    await waitFor(() => expect(dialog.getByRole('button', { name: 'Test' })).toBeEnabled())
  },
}

export const DeleteConnectionConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByText('Home Assistant')
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Delete connection' }))[2])
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText('Delete Home Assistant?')).toBeVisible()
    await expect(await dialog.findByText("Nothing is sent to it any more. This can't be undone.")).toBeVisible()
  },
}

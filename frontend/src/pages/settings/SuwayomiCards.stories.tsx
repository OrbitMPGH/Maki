import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith } from '../../../.storybook/mocks/api'
import { sources, suwayomiConnection } from '../../../.storybook/mocks/settings/data'
import { SuwayomiCard } from './SuwayomiCards'

const meta = {
  component: SuwayomiCard,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 860 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof SuwayomiCard>

export default meta
type Story = StoryObj<typeof meta>

export const Connected: Story = {
  play: async ({ canvas }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://suwayomi:4567'))
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Username' })).toHaveValue('maki'))
    await expect(await canvas.findByText('3 Suwayomi sources, 1 switched on.')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Manage sources' })).toBeEnabled())
  },
}

export const NotConnected: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/suwayomi'), () => HttpResponse.json({ url: null, authMode: 'none', username: null, passwordSet: false })),
      http.get(api('/search/sources'), () => HttpResponse.json(sources.filter((s) => s.provider !== 'suwayomi'))),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Not connected.')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Test' })).toBeDisabled())
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Manage sources' })).toBeDisabled())
    await expect(canvas.queryByRole('textbox', { name: 'Username' })).toBeNull()
  },
}

export const ConnectedWithoutSources: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/search/sources'), () => HttpResponse.json(sources.filter((s) => s.provider !== 'suwayomi')))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No Suwayomi sources yet.')).toBeVisible()
  },
}

export const AuthModeShowsCredentials: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/suwayomi'), () => HttpResponse.json({ ...suwayomiConnection, authMode: 'none', username: null, passwordSet: false })),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'URL' })).toHaveValue('http://suwayomi:4567'))
    await userEvent.click(await canvas.findByLabelText('Sign-in'))
    await userEvent.click(await within(document.body).findByRole('option', { name: 'Basic auth' }))
    await expect(await canvas.findByRole('textbox', { name: 'Username' })).toBeVisible()
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
  },
}

export const TestConnection: Story = {
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://suwayomi:4567'))
    await userEvent.click(await canvas.findByRole('button', { name: 'Test' }))
    await expect(await within(document.body).findByText('Suwayomi v2.1.1 answered with 12 sources installed.')).toBeVisible()
  },
}

export const TestConnectionFails: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/settings/suwayomi/test'), () => failWith(502, 'Suwayomi did not answer.'))),
  },
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://suwayomi:4567'))
    await userEvent.click(await canvas.findByRole('button', { name: 'Test' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Test' })).toBeEnabled())
  },
}

export const ChangingTheUrlWarnsAboutThePassword: Story = {
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://suwayomi:4567'))
    await userEvent.type(url, '/suwayomi')
    await expect(await canvas.findByText('A new URL clears the saved password, so enter it again.')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Manage sources' })).toBeDisabled())
  },
}

export const ManageSourcesOpensModal: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const manage = await canvas.findByRole('button', { name: 'Manage sources' })
    await waitFor(() => expect(manage).toBeEnabled())
    await userEvent.click(manage)
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(await within(dialog).findByText('Multi Archive')).toBeVisible()
  },
}

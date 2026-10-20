import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith } from '../../../.storybook/mocks/api'
import { IndexerForm } from './IndexerForm'

const meta = {
  component: IndexerForm,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 900 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof IndexerForm>

export default meta
type Story = StoryObj<typeof meta>

export const Saved: Story = {
  play: async ({ canvas }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.20:9696'))
    await expect(canvas.getByRole('button', { name: 'Save' })).toBeDisabled()
  },
}

export const EditEnablesSave: Story = {
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.20:9696'))
    await userEvent.type(url, '/prowlarr')
    await expect(await canvas.findByText('Unsaved changes')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Save' })).toBeEnabled()
  },
}

export const SwitchToJackett: Story = {
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'URL' })).toHaveValue('http://192.168.1.20:9696'))
    await userEvent.click(canvas.getByLabelText('Indexer', { selector: 'input' }))
    await userEvent.click(await within(document.body).findByRole('option', { name: 'Jackett' }))
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'URL' })).toHaveValue(''))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Save' })).toBeEnabled())
  },
}

export const TestConnection: Story = {
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'URL' })).toHaveValue('http://192.168.1.20:9696'))
    await userEvent.click(canvas.getByRole('button', { name: 'Test' }))
    await expect(await within(document.body).findByText('Prowlarr is reachable')).toBeVisible()
  },
}

export const TestFails: Story = {
  parameters: { msw: settingsMsw(http.post(api('/settings/indexer/test'), () => failWith(502, 'Prowlarr refused the API key.'))) },
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'URL' })).toHaveValue('http://192.168.1.20:9696'))
    await userEvent.click(canvas.getByRole('button', { name: 'Test' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Test' })).toBeEnabled())
    await expect(within(document.body).queryByText('Prowlarr is reachable')).toBeNull()
  },
}

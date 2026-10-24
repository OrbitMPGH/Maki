import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http } from 'msw'
import { settingsMsw } from '../../.storybook/mocks/settings'
import { api, failWith } from '../../.storybook/mocks/api'
import { ConnectionForm, ConnectionSettingsCard } from './ConnectionSettingsCard'

const meta = {
  component: ConnectionSettingsCard,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  args: {
    name: 'flaresolverr',
    title: 'FlareSolverr',
    description: 'Solves Cloudflare challenges for sources that need it.',
    fields: [{ key: 'url', label: 'URL', placeholder: 'http://localhost:8191' }],
  },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 860 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ConnectionSettingsCard>

export default meta
type Story = StoryObj<typeof meta>

export const Saved: Story = {
  play: async ({ canvas }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.10:8191'))
    await expect(canvas.queryByText('Unsaved')).toBeNull()
  },
}

export const EditDiscardAndSave: Story = {
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.10:8191'))
    await userEvent.type(url, '/v1')
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Discard' }))
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.10:8191'))
    await userEvent.type(url, '/v1')
    await userEvent.click(await canvas.findByRole('button', { name: 'Save' }))
    await expect(await within(document.body).findByText('Saved')).toBeVisible()
  },
}

export const TestSucceeds: Story = {
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.10:8191'))
    await userEvent.click(await canvas.findByRole('button', { name: 'Test' }))
    await expect(await within(document.body).findByText('FlareSolverr is reachable')).toBeVisible()
  },
}

export const TestFails: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/settings/flaresolverr/test'), () => failWith(502, 'Connection refused'))),
  },
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.10:8191'))
    await userEvent.click(await canvas.findByRole('button', { name: 'Test' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Test' })).toBeEnabled())
    await expect(within(document.body).queryByText('FlareSolverr is reachable')).toBeNull()
  },
}

export const SecretAndSelectFields: Story = {
  args: {
    name: 'downloadclient',
    title: 'Download client',
    description: 'The torrent client volume releases are sent to.',
    fields: [
      {
        key: 'kind',
        label: 'Client',
        options: [
          { value: 'qbittorrent', label: 'qBittorrent' },
          { value: 'deluge', label: 'Deluge' },
        ],
      },
      { key: 'url', label: 'URL' },
      { key: 'password', label: 'Password', secret: true },
      { key: 'label', label: 'Label', description: 'Category given to torrents Maki adds.' },
    ],
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByLabelText('Client')).toHaveValue('qBittorrent'))
    await waitFor(() => expect(canvas.getByLabelText('Password')).toHaveAttribute('type', 'password'))
  },
}

export const FieldsDependOnValues: Story = {
  args: {
    name: 'downloadclient',
    title: 'Download client',
    description: 'The torrent client volume releases are sent to.',
    fields: (values) => [
      { key: 'kind', label: 'Client', options: [{ value: 'qbittorrent', label: 'qBittorrent' }, { value: 'deluge', label: 'Deluge' }] },
      { key: 'url', label: 'URL' },
      ...(values.kind === 'deluge' ? [] : [{ key: 'username', label: 'Username' }]),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('textbox', { name: 'Username' })).toBeVisible()
  },
}

export const WithChildren: Story = {
  args: { children: <p>Extra content a card can render under its fields.</p> },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Extra content a card can render under its fields.')).toBeVisible()
  },
}

export const BareForm: Story = {
  render: (args) => <ConnectionForm name={args.name} title={args.title} fields={args.fields} />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Test' })).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Save' })).toBeDisabled())
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith } from '../../../.storybook/mocks/api'
import { importListsSettings, scrobbleSettings } from '../../../.storybook/mocks/settings/data'
import { ImportListSettingsSection, KavitaCard, ScrobbleSection } from './IntegrationCards'

const meta = {
  component: ScrobbleSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 860 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ScrobbleSection>

export default meta
type Story = StoryObj<typeof meta>

export const TrackersAdmin: Story = {
  play: async ({ canvas }) => {
    const clientId = await canvas.findAllByRole('textbox', { name: 'Client ID' })
    await waitFor(() => expect(clientId[0]).toHaveValue('48211'))
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Sync interval (minutes)' })).toHaveValue('30'))
  },
}

export const TrackersReader: Story = {
  parameters: {
    auth: 'reader',
    msw: settingsMsw(http.get(api('/settings/scrobble'), () => HttpResponse.json({ ...scrobbleSettings, isAdmin: false }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByLabelText('Personal Access Token')).toBeInTheDocument()
    await expect(canvas.queryByRole('textbox', { name: 'Client ID' })).toBeNull()
    await expect(canvas.queryByRole('textbox', { name: 'Sync interval (minutes)' })).toBeNull()
  },
}

export const TrackersEditShowsSave: Story = {
  play: async ({ canvas, userEvent }) => {
    const email = await canvas.findByRole('textbox', { name: 'Email' })
    await waitFor(() => expect(email).toHaveValue('rin@example.test'))
    await userEvent.type(email, '.au')
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
  },
}

export const TrackersPlanToRead: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/settings/scrobble'), () => HttpResponse.json({ ...scrobbleSettings, planToRead: true }))),
  },
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Add unread series as plan-to-read/ })
    await waitFor(() => expect(toggle).toBeChecked())
  },
}

export const KavitaConnected: Story = {
  render: () => <KavitaCard />,
  play: async ({ canvas }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.20:5000'))
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Path as Maki sees it' })).toHaveValue('/library'))
    await waitFor(() => expect(canvas.getByLabelText("Attribute Kavita's reading to")).toHaveValue('Maki'))
  },
}

export const KavitaLibraryFilter: Story = {
  render: () => <KavitaCard />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Manga')).toBeVisible()
  },
}

export const KavitaNotSetUp: Story = {
  render: () => <KavitaCard />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/kavita'), () =>
        HttpResponse.json({ url: null, apiKey: null, pathMapFrom: null, pathMapTo: null, userId: null }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Set up the Kavita connection above to pick libraries.')).toBeVisible()
  },
}

export const KavitaLibrariesUnavailable: Story = {
  render: () => <KavitaCard />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/kavita/libraries'), () => failWith(502, 'Kavita did not answer.'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Could not load libraries from Kavita/)).toBeVisible()
  },
}

export const KavitaTestConnection: Story = {
  render: () => <KavitaCard />,
  play: async ({ canvas, userEvent }) => {
    const url = await canvas.findByRole('textbox', { name: 'URL' })
    await waitFor(() => expect(url).toHaveValue('http://192.168.1.20:5000'))
    await userEvent.click(await canvas.findByRole('button', { name: 'Test' }))
    await expect(await within(document.body).findByText('Kavita is reachable')).toBeVisible()
  },
}

export const ImportListsAdmin: Story = {
  render: () => <ImportListSettingsSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('switch', { name: 'Enable import lists for everyone' })).toBeInTheDocument()
    await expect(await canvas.findByText('Cloudless Archipelago')).toBeVisible()
  },
}

export const ImportListsReader: Story = {
  render: () => <ImportListSettingsSection />,
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Cloudless Archipelago')).toBeVisible()
    await expect(canvas.queryByRole('switch', { name: 'Enable import lists for everyone' })).toBeNull()
  },
}

export const ImportListsDisabledInstanceWide: Story = {
  render: () => <ImportListSettingsSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/importlists'), () => HttpResponse.json({ ...importListsSettings, enabled: false })),
    ),
  },
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: 'Enable import lists for everyone' })
    await waitFor(() => expect(toggle).not.toBeChecked())
  },
}

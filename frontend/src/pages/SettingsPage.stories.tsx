import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { AfterSignIn } from '../../.storybook/AfterSignIn'
import { settingsMsw } from '../../.storybook/mocks/settings'
import { api, failWith } from '../../.storybook/mocks/api'
import SettingsPage from './SettingsPage'


const meta = {
  component: SettingsPage,
  tags: ['ai-generated'],
  parameters: {
    layout: 'fullscreen',
    router: { path: '/settings', initialEntries: ['/settings'] },
    msw: settingsMsw(),
  },
} satisfies Meta<typeof SettingsPage>

export default meta
type Story = StoryObj<typeof meta>

const tabNames = ['Account', 'Preferences', 'Reading', 'Library', 'Downloads', 'Integrations', 'Users & security', 'System']

export const AdminAllTabs: Story = {
  play: async ({ canvas }) => {
    for (const name of tabNames) {
      await expect(await canvas.findByRole('tab', { name })).toBeVisible()
    }
    await expect(await canvas.findByRole('heading', { name: 'Sign-in' })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Run setup guide' })).toBeVisible()
  },
}

export const SwitchTab: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('tab', { name: 'Library' }))
    await expect(await canvas.findByRole('heading', { name: 'Root folders' })).toBeVisible()
    await expect(await canvas.findByText('/library/manga')).toBeVisible()
    await expect(canvas.queryByRole('heading', { name: 'Sign-in' })).toBeNull()
  },
}

export const PreferencesTab: Story = {
  parameters: { router: { path: '/settings', initialEntries: ['/settings?tab=preferences'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Appearance' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Push notifications' })).toBeVisible()
  },
}

export const ReadingTab: Story = {
  parameters: { router: { path: '/settings', initialEntries: ['/settings?tab=reading'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Kavita read sync' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'OPDS catalogue' })).toBeVisible()
  },
}

export const LibraryTab: Story = {
  parameters: { router: { path: '/settings', initialEntries: ['/settings?tab=library'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Metadata database' })).toBeVisible()
    await expect(await canvas.findByText(/Snapshot on disk/)).toBeVisible()
  },
}

export const DownloadsTab: Story = {
  parameters: { router: { path: '/settings', initialEntries: ['/settings?tab=downloads'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Download queue' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Upgrade scan' })).toBeVisible()
  },
}

export const IntegrationsTab: Story = {
  parameters: { router: { path: '/settings', initialEntries: ['/settings?tab=integrations'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Kavita' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Suwayomi' })).toBeVisible()
  },
}

export const UsersTab: Story = {
  parameters: { router: { path: '/settings', initialEntries: ['/settings?tab=users'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Noa')).toBeVisible()
  },
}

export const SystemTab: Story = {
  render: () => (
    <AfterSignIn>
      <SettingsPage />
    </AfterSignIn>
  ),
  parameters: { router: { path: '/settings', initialEntries: ['/settings?tab=system'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Backup & restore' })).toBeVisible()
    await waitFor(() => expect((canvas.getAllByRole('button', { name: 'Restore' })).length).toBe(3))
    await waitFor(() =>
      expect(canvas.getByRole('switch', { name: 'Analyze new files and run daily reconciliation' })).toBeChecked(),
    )
  },
}

export const DeepLinkToCard: Story = {
  render: () => (
    <AfterSignIn>
      <SettingsPage />
    </AfterSignIn>
  ),
  parameters: { router: { path: '/settings', initialEntries: ['/settings?s=backup'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('tab', { name: 'System', selected: true })).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('heading', { name: 'Backup & restore' })).toBeVisible())
  },
}

export const LegacyTabAlias: Story = {
  render: () => (
    <AfterSignIn>
      <SettingsPage />
    </AfterSignIn>
  ),
  parameters: { router: { path: '/settings', initialEntries: ['/settings?tab=connections'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('tab', { name: 'Downloads', selected: true })).toBeVisible()
  },
}

export const Reader: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('tab', { name: 'Account' })).toBeVisible()
    await expect(await canvas.findByRole('tab', { name: 'Preferences' })).toBeVisible()
    await expect(canvas.queryByRole('tab', { name: 'Downloads' })).toBeNull()
    await expect(canvas.queryByRole('tab', { name: 'System' })).toBeNull()
    await expect(canvas.queryByRole('button', { name: 'Run setup guide' })).toBeNull()
  },
}

export const ReaderPreferences: Story = {
  parameters: { auth: 'reader', router: { path: '/settings', initialEntries: ['/settings?tab=preferences'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Appearance' })).toBeVisible()
    await expect(canvas.queryByRole('heading', { name: 'Content rating' })).toBeNull()
  },
}

export const ReaderReading: Story = {
  parameters: { auth: 'reader', router: { path: '/settings', initialEntries: ['/settings?tab=reading'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'OPDS catalogue' })).toBeVisible()
  },
}

export const UnsavedChangesGuard: Story = {
  parameters: { router: { path: '/settings', initialEntries: ['/settings?tab=downloads'] } },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const field = await canvas.findByRole('textbox', { name: 'Concurrent chapter downloads' })
    await waitFor(() => expect(field).toHaveValue('2'))
    await userEvent.clear(field)
    await userEvent.type(field, '4')
    await userEvent.click(await canvas.findByRole('tab', { name: 'Account' }))
    await expect(await body.findByText('Discard unsaved changes?')).toBeVisible()
    await userEvent.click(await body.findByRole('button', { name: 'Keep editing' }))
    await waitFor(() => expect(body.queryByText('Discard unsaved changes?')).toBeNull())
    await expect(await canvas.findByRole('tab', { name: 'Downloads', selected: true })).toBeVisible()
  },
}

export const UsersFailedToLoad: Story = {
  parameters: {
    router: { path: '/settings', initialEntries: ['/settings?tab=users'] },
    msw: settingsMsw(http.get(api('/users'), () => failWith(500)), http.get(api('/users/auditlog'), () => failWith(500))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('tab', { name: 'Users & security', selected: true })).toBeVisible()
  },
}

const upgraded = settingsMsw(
  http.get(api('/system/status'), () => HttpResponse.json({ version: '0.37.0', upgradedFrom: '0.36.0' })),
)

export const NewTab: Story = {
  parameters: { msw: upgraded, router: { path: '/settings', initialEntries: ['/settings?tab=new'] } },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('tab', { name: 'New', selected: true })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Reading status New' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Drop folder New' })).toBeVisible()
    await expect(canvas.queryByRole('heading', { name: /^Sign-in/ })).toBeNull()
    await expect(await canvas.findByRole('heading', { name: 'Volume releases' })).toBeVisible()
    await expect(await canvas.findByRole('switch', { name: /Approve volume releases automatically/ })).toHaveAccessibleName(/^Approve volume releases automatically New /)
    // Volume releases is not new itself, so only its new controls show.
    await waitFor(() => expect(canvas.getByText('Auto-grab size limit')).not.toBeVisible())
    await expect(await canvas.findByText(/^New in here: Skip the recap of the previous chapter, Hide credit pages/)).toBeVisible()
    const inReading = await canvas.findAllByRole('link', { name: 'In Reading' })
    await userEvent.click(inReading.find((a) => a.getAttribute('href')?.endsWith('s=reading-status'))!)
    await expect(await canvas.findByRole('tab', { name: 'Reading', selected: true })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Reading status New' })).toBeVisible()
  },
}

export const NewTabOpensOnAccount: Story = {
  parameters: { msw: upgraded },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('tab', { name: 'New' })).toBeVisible()
    await expect(await canvas.findByRole('tab', { name: 'Account', selected: true })).toBeVisible()
  },
}

export const NoNewTabOnFreshInstall: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/system/status'), () => HttpResponse.json({ version: '0.37.0', upgradedFrom: null }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Sign-in' })).toBeVisible()
    await expect(canvas.queryByRole('tab', { name: 'New' })).toBeNull()
  },
}

export const NestedSettingDeepLink: Story = {
  render: () => (
    <AfterSignIn>
      <SettingsPage />
    </AfterSignIn>
  ),
  parameters: { msw: upgraded, router: { path: '/settings', initialEntries: ['/settings?s=hide-credit-pages'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('tab', { name: 'Reading', selected: true })).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('heading', { name: 'Reader profiles' })).toBeVisible())
  },
}

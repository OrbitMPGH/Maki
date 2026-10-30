import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith, hoursAgo } from '../../../.storybook/mocks/api'
import {
  backupSettings,
  imageCache,
  updateSettings,
  updateStatusAvailable,
  updateStatusCurrent,
} from '../../../.storybook/mocks/settings/data'
import { AfterSignIn } from '../../../.storybook/AfterSignIn'
import { BackupSection, HealthSettingsSection, ImageCacheSection, UpdatesSection } from './SystemCards'

const meta = {
  component: BackupSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 860 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof BackupSection>

export default meta
type Story = StoryObj<typeof meta>

async function dialogOf(canvasElement: HTMLElement) {
  const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
  await waitFor(() => expect(dialog).toBeVisible())
  return within(dialog)
}

export const Backups: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect((canvas.getAllByRole('button', { name: 'Restore' })).length).toBe(3))
    await waitFor(() => expect(canvas.getAllByText('Scheduled')).toHaveLength(2))
    await expect(await canvas.findByText('Manual')).toBeVisible()
    // The schedule switch reads a different request than the backup list, which can land first.
    await waitFor(() => expect(canvas.getByRole('switch', { name: /^Back up on a schedule/ })).toBeChecked())
  },
}

export const NoBackupsYet: Story = {
  parameters: { msw: settingsMsw(http.get(api('/system/backups'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Back up now' })).toBeVisible()
    await expect(canvas.queryByRole('table')).toBeNull()
  },
}

export const BackUpNow: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Back up now' }))
    await expect(await within(document.body).findByText('Backup created')).toBeVisible()
  },
}

export const RestoreConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [first] = await canvas.findAllByRole('button', { name: 'Restore' })
    await userEvent.click(first)
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByRole('button', { name: 'Restore & restart' })).toBeVisible()
    await userEvent.click(await dialog.findByRole('button', { name: 'Restore & restart' }))
    await expect(await within(document.body).findByText('Restore staged')).toBeVisible()
  },
}

export const RestoreFromUploadedFile: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('button', { name: 'Restore from file…' })
    const input = canvasElement.querySelector<HTMLInputElement>('input[type="file"]')!
    await userEvent.upload(input, new File(['zip'], 'maki-from-laptop.zip', { type: 'application/zip' }))
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText('maki-from-laptop.zip')).toBeVisible()
  },
}

export const RestoreRefused: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/system/backups/:name/restore'), () => failWith(409, 'This backup was made by a newer version of Maki.'))),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [first] = await canvas.findAllByRole('button', { name: 'Restore' })
    await userEvent.click(first)
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await dialog.findByRole('button', { name: 'Restore & restart' }))
    await expect(await within(document.body).findByText('This backup was made by a newer version of Maki.')).toBeVisible()
  },
}

export const DeleteBackupConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const [first] = await canvas.findAllByRole('button', { name: 'Delete backup' })
    await userEvent.click(first)
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByRole('button', { name: 'Delete backup' })).toBeVisible()
  },
}

export const ScheduleOff: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/settings/backup'), () => HttpResponse.json({ ...backupSettings, scheduled: false }))),
  },
  play: async ({ canvas, userEvent }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Back up on a schedule/ })
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Backups to keep' })).toHaveValue('7'))
    await waitFor(() => expect(toggle).not.toBeChecked())
    await userEvent.click(toggle)
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
  },
}

export const UpToDate: Story = {
  render: () => <UpdatesSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Up to date, last checked/)).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('switch', { name: /^Check for updates/ })).toBeChecked())
  },
}

export const UpdateAvailableWithNotes: Story = {
  render: () => <UpdatesSection />,
  parameters: { msw: settingsMsw(http.get(api('/system/update'), () => HttpResponse.json(updateStatusAvailable))) },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('Update available: 0.33.0')).toBeVisible()
    const notes = await canvas.findByRole('button', { name: /What's new/ })
    await waitFor(() => expect(notes).toHaveAttribute('aria-expanded', 'false'))
    await userEvent.click(notes)
    await waitFor(() => expect(notes).toHaveAttribute('aria-expanded', 'true'))
    const bullet = await canvas.findByText('Offline reading keeps your place')
    await waitFor(() => expect(bullet).toBeVisible())
  },
}

export const DesktopUpdateDownloading: Story = {
  render: () => <UpdatesSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/system/update'), () => HttpResponse.json({ ...updateStatusAvailable, isDocker: false, runtime: 'desktop' })),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Downloading the update in the background.')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Restart to update' })).toBeDisabled())
  },
}

export const DesktopUpdateReady: Story = {
  render: () => <UpdatesSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/system/update'), () =>
        HttpResponse.json({ ...updateStatusAvailable, isDocker: false, runtime: 'desktop', updateReady: true }),
      ),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText(/Maki downloads updates in the background/)).toBeVisible()
    const restart = await canvas.findByRole('button', { name: 'Restart to update' })
    await waitFor(() => expect(restart).toBeEnabled())
    await userEvent.click(restart)
    await expect(await within(document.body).findByText(/Installing the update/)).toBeVisible()
  },
}

export const DevBuild: Story = {
  render: () => <UpdatesSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/system/update'), () => HttpResponse.json({ ...updateStatusCurrent, isDevBuild: true, isDocker: false, checkedAt: null })),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Unofficial build, update checks are skipped.')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Check now' })).toBeDisabled())
  },
}

export const NeverChecked: Story = {
  render: () => <UpdatesSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/system/update'), () => HttpResponse.json({ ...updateStatusCurrent, checkedAt: null })),
      http.get(api('/settings/updates'), () => HttpResponse.json({ ...updateSettings, checkForUpdates: false })),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Not checked yet')).toBeVisible()
    const toggle = await canvas.findByRole('switch', { name: /^Check for updates/ })
    await waitFor(() => expect(toggle).not.toBeChecked())
  },
}

export const CheckNowFindsNothing: Story = {
  render: () => <UpdatesSection />,
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Check now' }))
    await expect(await within(document.body).findByText('Already up to date')).toBeVisible()
  },
}

export const CheckNowFindsRelease: Story = {
  render: () => <UpdatesSection />,
  parameters: {
    msw: settingsMsw(http.post(api('/settings/updates/check'), () => HttpResponse.json(updateStatusAvailable))),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Check now' }))
    await expect(await within(document.body).findByText('Maki 0.33.0 is available')).toBeVisible()
  },
}

export const ImageCacheIdle: Story = {
  render: () => <ImageCacheSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Posters: 96 files/)).toBeVisible()
    await expect(await canvas.findByText(/2 of 98 series have no usable poster/)).toBeVisible()
    await expect(await canvas.findByText(/Last run .*: 0 posters downloaded/)).toBeVisible()
  },
}

export const ImageCacheEverythingPresent: Story = {
  render: () => <ImageCacheSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/system/image-cache'), () =>
        HttpResponse.json({ ...imageCache, usage: { ...imageCache.usage, coversMissing: 0 }, status: { ...imageCache.status, finishedAt: null } }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Every series has a poster.')).toBeVisible()
    await expect(await canvas.findByText('Not run yet')).toBeVisible()
  },
}

export const ImageCacheRunning: Story = {
  render: () => <ImageCacheSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/system/image-cache'), () =>
        HttpResponse.json({
          ...imageCache,
          status: { ...imageCache.status, running: true, phase: 'covers', processed: 31, total: 98, startedAt: hoursAgo(0.1), finishedAt: null },
        }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Rebuilding posters, 31 of 98')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Rebuild missing' })).toBeDisabled())
    await expect(await canvas.findByRole('progressbar')).toBeVisible()
  },
}

export const ImageCacheLastRunFailed: Story = {
  render: () => <ImageCacheSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/system/image-cache'), () =>
        HttpResponse.json({ ...imageCache, status: { ...imageCache.status, lastError: 'The metadata service rate limited the run.' } }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Last run failed: The metadata service rate limited the run./)).toBeVisible()
  },
}

export const ImageCacheRebuildAllConfirm: Story = {
  render: () => <ImageCacheSection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Rebuild all' }))
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText(/This re-downloads the poster for all 98 series/)).toBeVisible()
    await userEvent.click(await dialog.findByRole('button', { name: 'Rebuild all' }))
    await expect(await within(document.body).findByText('Rebuilding image cache')).toBeVisible()
  },
}

export const LibraryHealth: Story = {
  render: () => (
    <AfterSignIn>
      <HealthSettingsSection />
    </AfterSignIn>
  ),
  play: async ({ canvas }) => {
    await waitFor(() =>
      expect(canvas.getByRole('switch', { name: 'Analyze new files and run daily reconciliation' })).toBeChecked(),
    )
    await expect(await canvas.findByLabelText('Low disk warning (%)')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Save' })).toBeNull()
  },
}

export const LibraryHealthEditShowsSave: Story = {
  render: () => (
    <AfterSignIn>
      <HealthSettingsSection />
    </AfterSignIn>
  ),
  play: async ({ canvas, userEvent }) => {
    const hour = await canvas.findByLabelText('Daily scan hour')
    await userEvent.clear(hour)
    await userEvent.type(hour, '5')
    await userEvent.tab()
    const save = await canvas.findByRole('button', { name: 'Save' })
    await userEvent.click(save)
    await expect(await within(document.body).findByText('Saved')).toBeVisible()
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'Save' })).toBeNull())
  },
}

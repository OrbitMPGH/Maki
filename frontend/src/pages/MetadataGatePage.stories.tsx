import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import type { DumpProgress, MetadataSettings } from '../api/hooks'
import { api } from '../../.storybook/mocks/api'
import { downloadingDump, idleDump } from '../../.storybook/mocks/shell/wizard'
import MetadataGatePage from './MetadataGatePage'

const missing: MetadataSettings = { dumpPresent: false, dumpSizeBytes: null, dumpRefreshedAt: null }

const failedDump: DumpProgress = {
  ...idleDump,
  finishedAt: '2026-10-19T11:58:00Z',
  lastError: 'No space left on device',
}

const refresh = fn()

function gate(progress: DumpProgress) {
  return [
    http.get(api('/settings/metadata'), () => HttpResponse.json(missing)),
    http.get(api('/settings/metadata/dump-progress'), () => HttpResponse.json(progress)),
    http.post(api('/settings/metadata/refresh'), () => {
      refresh()
      return HttpResponse.json({ started: true, alreadyRunning: false })
    }),
  ]
}

const meta = {
  component: MetadataGatePage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', msw: gate(downloadingDump) },
  beforeEach: () => {
    refresh.mockClear()
  },
} satisfies Meta<typeof MetadataGatePage>

export default meta
type Story = StoryObj<typeof meta>

export const AdminDownloading: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Getting the metadata database ready' })).toBeVisible()
    await expect(await canvas.findByText('Downloading metadata database')).toBeVisible()
    await expect(await canvas.findByText(/207 MB of 333 MB/)).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Try again' })).toBeNull()
  },
}

export const AdminDownloadFailed: Story = {
  parameters: { msw: gate(failedDump) },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('The download failed: No space left on device')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(refresh).toHaveBeenCalledTimes(1))
  },
}

export const AdminNotStarted: Story = {
  parameters: { msw: gate(idleDump) },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('The download has not started yet.')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Start now' }))
    await waitFor(() => expect(refresh).toHaveBeenCalledTimes(1))
  },
}

/** A reader can't see the transfer or retry it; the page polls and opens the app when it lands. */
export const ReaderWaiting: Story = {
  parameters: { auth: 'reader', msw: [http.get(api('/settings/metadata'), () => HttpResponse.json(missing))] },
  play: async ({ canvas }) => {
    await expect(
      await canvas.findByText('The server is downloading it now. This page opens Maki by itself when it is done.'),
    ).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Try again' })).toBeNull()
    await expect(canvas.queryByText('Downloading metadata database')).toBeNull()
    await expect(await canvas.findByRole('button', { name: 'Sign out' })).toBeVisible()
  },
}

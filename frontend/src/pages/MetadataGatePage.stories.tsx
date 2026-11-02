import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import type { DumpProgress, MetadataSettings } from '../api/hooks'
import { api } from '../../.storybook/mocks/api'
import { continueReading } from '../../.storybook/mocks/home'
import { downloadingDump, idleDump } from '../../.storybook/mocks/shell/wizard'
import MetadataGatePage from './MetadataGatePage'

const missing: MetadataSettings = { dumpPresent: false, dumpSizeBytes: null, dumpRefreshedAt: null, dumpDownloadFailed: false }

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
    await expect(await canvas.findByRole('link', { name: 'Open settings' })).toBeVisible()
    await expect(await canvas.findByRole('link', { name: 'Open health' })).toBeVisible()
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
    await expect(canvas.queryByRole('link', { name: 'Open settings' })).toBeNull()
    await expect(await canvas.findByRole('button', { name: 'Sign out' })).toBeVisible()
  },
}

/** After a failed download a reader is told so, not that it is downloading, and never sees the error text. */
export const ReaderDownloadFailed: Story = {
  parameters: {
    auth: 'reader',
    msw: [
      http.get(api('/settings/metadata'), () => HttpResponse.json({ ...missing, dumpDownloadFailed: true })),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/The download failed\. Maki tries again by itself/)).toBeVisible()
    await expect(canvas.queryByText(/The server is downloading it now/)).toBeNull()
    await expect(canvas.queryByRole('button', { name: 'Try again' })).toBeNull()
  },
}

/** The reader is the one page left open, so the gate lists what there is to pick up, six at most. */
export const ReaderCanPickUpReading: Story = {
  parameters: { auth: 'reader', msw: [http.get(api('/settings/metadata'), () => HttpResponse.json(missing))] },
  play: async ({ canvas }) => {
    const list = await canvas.findByRole('region', { name: 'Continue reading' })
    const first = continueReading[0]
    await expect(await within(list).findByRole('link', { name: new RegExp(first.seriesTitle) })).toHaveAttribute(
      'href',
      `/read/${first.chapterId}`,
    )
    await waitFor(() => expect(within(list).getAllByRole('link')).toHaveLength(6))
  },
}

export const NothingToResume: Story = {
  parameters: {
    auth: 'reader',
    msw: [
      http.get(api('/settings/metadata'), () => HttpResponse.json(missing)),
      http.get(api('/home/reading'), () => HttpResponse.json({ continueReading: [] })),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/The server is downloading it now/)).toBeVisible()
    await expect(canvas.queryByRole('region', { name: 'Continue reading' })).toBeNull()
  },
}

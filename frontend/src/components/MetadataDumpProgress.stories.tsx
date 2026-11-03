import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { notifications } from '@mantine/notifications'
import { api } from '../../.storybook/mocks/api'
import type { DumpProgress } from '../api/hooks'
import { downloadingDump, idleDump } from '../../.storybook/mocks/shell/wizard'
import MetadataDumpProgress, { DumpProgressBar } from './MetadataDumpProgress'

const meta = {
  component: MetadataDumpProgress,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  // The toast keeps one id, and Mantine ignores a show() for an id still in the store.
  beforeEach: () => {
    notifications.clean()
    return () => notifications.clean()
  },
} satisfies Meta<typeof MetadataDumpProgress>

export default meta
type Story = StoryObj<typeof meta>

const serving = (progress: DumpProgress) => [
  http.get(api('/settings/metadata/dump-progress'), () => HttpResponse.json(progress)),
]

// The component renders nothing itself; it owns a toast pinned to the bottom right.
export const ToastWhileDownloading: Story = {
  parameters: { msw: serving(downloadingDump) },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText('Downloading metadata database')).toBeVisible()
    await expect(await body.findByText('62%')).toBeVisible()
    await expect(await body.findByText(/207 MB of 333 MB/)).toBeVisible()
  },
}

const indexingDump: DumpProgress = {
  ...downloadingDump,
  phase: 'indexing',
  indexProgress: 0.42,
  bytesPerSecond: null,
  estimatedSecondsRemaining: 75,
}

export const ToastWhileIndexing: Story = {
  parameters: { msw: serving(indexingDump) },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText('Building metadata search indexes')).toBeVisible()
    await expect(await body.findByText('42%')).toBeVisible()
    await expect(await body.findByText(/left$/)).toBeVisible()
  },
}

export const QuietWhenIdle: Story = {
  parameters: { msw: serving(idleDump) },
  play: async ({ canvasElement }) => {
    // Give the progress request time to answer before checking that no toast appeared.
    await new Promise((resolve) => setTimeout(resolve, 150))
    const body = within(canvasElement.ownerDocument.body)
    await expect(body.queryByText('Downloading metadata database')).toBeNull()
  },
}

export const BarDownloading: StoryObj<typeof DumpProgressBar> = {
  render: () => <DumpProgressBar progress={downloadingDump} />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Downloading metadata database')).toBeVisible()
    await expect(await canvas.findByRole('progressbar', { name: 'Downloading metadata database' })).toBeVisible()
  },
}

export const BarWithoutTotal: StoryObj<typeof DumpProgressBar> = {
  render: () => (
    <DumpProgressBar
      progress={{ ...downloadingDump, totalBytes: null, bytesPerSecond: null, estimatedSecondsRemaining: null }}
    />
  ),
}

export const BarIndexing: StoryObj<typeof DumpProgressBar> = {
  render: () => <DumpProgressBar progress={indexingDump} />,
}

export const BarIndexingStarting: StoryObj<typeof DumpProgressBar> = {
  render: () => <DumpProgressBar progress={{ ...indexingDump, indexProgress: 0.01, estimatedSecondsRemaining: null }} />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/only happens once per snapshot/)).toBeVisible()
  },
}

export const BarInstalling: StoryObj<typeof DumpProgressBar> = {
  render: () => <DumpProgressBar progress={{ ...downloadingDump, phase: 'installing' }} />,
}

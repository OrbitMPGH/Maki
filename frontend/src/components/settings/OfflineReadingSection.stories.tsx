import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, daysAgo } from '../../../.storybook/mocks/api'
import type { ChapterDto } from '../../api/types'
import { deleteUserRecords, putOfflineChapter } from '../../pwa/db'
import type { OfflineChapter } from '../../pwa/contract'
import { loadOfflineChapters } from '../../pwa/offlineChapters'
import { OfflineReadingSection } from './OfflineReadingSection'

const ME = 1
const MB = 1024 * 1024

const chapter = (id: number, seriesId: number, number: number): ChapterDto => ({
  id,
  seriesId,
  number,
  numberRaw: String(number),
  volume: null,
  title: null,
  isOneShot: false,
  language: 'en',
  releaseDate: daysAgo(number),
  wanted: true,
  hasFile: true,
  fileRemovedAt: null,
  fileDeleteDueAt: null,
  filePath: null,
  fileSourceName: 'mangadex',
  fileReleaseName: null,
  fileVolume: null,
  fileQuality: null,
})

const chaptersBySeries: Record<number, ChapterDto[]> = {
  1: [chapter(1301, 1, 130), chapter(1311, 1, 131), chapter(1321, 1, 132)],
  3: [chapter(3041, 3, 41), chapter(3042, 3, 42)],
}

const saved = (chapterId: number, seriesId: number, patch: Partial<OfflineChapter> = {}): OfflineChapter => ({
  userId: ME,
  chapterId,
  seriesId,
  pageVersion: 'v1',
  pageCount: 24,
  bytes: 18 * MB,
  savedAt: daysAgo(1),
  reason: 'manual',
  state: 'saved',
  ...patch,
})

const records: OfflineChapter[] = [
  saved(1301, 1),
  saved(1311, 1, { reason: 'auto' }),
  saved(1321, 1, { state: 'stale' }),
  saved(3041, 3, { bytes: 22 * MB }),
  saved(3042, 3, { state: 'error', bytes: 0 }),
]

const chapterHandler = http.get(api('/chapter'), ({ request }) => {
  const seriesId = Number(new URL(request.url).searchParams.get('seriesId'))
  return HttpResponse.json(chaptersBySeries[seriesId] ?? [])
})

// The store lives in IndexedDB and a module-level snapshot, so a story seeds both and cleans up after.
const seed = (rows: OfflineChapter[]) => async () => {
  await deleteUserRecords(ME)
  for (const row of rows) await putOfflineChapter(row)
  await loadOfflineChapters(null)
  await loadOfflineChapters(ME)
  return async () => {
    await deleteUserRecords(ME)
    await loadOfflineChapters(null)
  }
}

const meta = {
  component: OfflineReadingSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw(chapterHandler) },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 820 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof OfflineReadingSection>

export default meta
type Story = StoryObj<typeof meta>

export const NothingSaved: Story = {
  beforeEach: seed([]),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Nothing is saved on this device yet/)).toBeVisible()
    await expect(await canvas.findByText('Storage on this device')).toBeVisible()
  },
}

export const WithSavedChapters: Story = {
  beforeEach: seed(records),
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('link', { name: 'Lanterns Over Kasumi' })).toHaveAttribute('href', '/series/1'))
    await expect(await canvas.findByText('Automatic')).toBeVisible()
    await expect(await canvas.findByText('Not saved')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Remove Lanterns Over Kasumi from this device' })).toBeVisible()
  },
}

export const RemoveEverythingConfirm: Story = {
  beforeEach: seed(records),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('link', { name: 'Lanterns Over Kasumi' })
    await userEvent.click(await canvas.findByRole('button', { name: 'Remove everything from this device' }))
    const dialogElement = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(dialogElement).toBeVisible())
    const dialog = within(dialogElement)
    await expect(await dialog.findByText('Remove everything from this device?')).toBeVisible()
    await userEvent.click(await dialog.findByRole('button', { name: 'Remove everything' }))
    await expect(await canvas.findByText(/Nothing is saved on this device yet/)).toBeVisible()
  },
}

export const RemoveOneSeries: Story = {
  beforeEach: seed(records),
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Remove Lanterns Over Kasumi from this device' }))
    await waitFor(() => expect(canvas.queryByRole('link', { name: 'Lanterns Over Kasumi' })).toBeNull())
    await expect(await canvas.findByRole('link', { name: 'Iron Petal Academy' })).toBeVisible()
  },
}

export const KeepUpcomingChoices: Story = {
  beforeEach: seed([]),
  play: async ({ canvas, userEvent }) => {
    const keep = await canvas.findByLabelText('Keep upcoming chapters')
    // Disabled until the stored choice has been read.
    await waitFor(() => expect(keep).toBeEnabled())
    await userEvent.click(keep)
    await expect(await within(document.body).findByRole('option', { name: 'The next 3 unread chapters' })).toBeVisible()
  },
}

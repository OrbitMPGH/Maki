import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, daysAgo, hoursAgo } from '../../../.storybook/mocks/api'
import { makeManifest } from '../../../.storybook/mocks/entry/fixtures'
import type { ReaderManifest } from '../../api/reader'
import type { OfflineChapter } from '../../pwa/contract'
import { OfflineHome } from './OfflineHome'

const manifests: ReaderManifest[] = [
  // Lanterns: chapter 11 finished, 12 half read, 13 untouched.
  makeManifest({ chapterId: 9011, seriesId: 1, label: 'Chapter 11', number: 11, completed: true }),
  makeManifest({ chapterId: 9012, seriesId: 1, label: 'Chapter 12', number: 12, resumePage: 9 }),
  makeManifest({ chapterId: 9013, seriesId: 1, label: 'Chapter 13', number: 13 }),
  makeManifest({ chapterId: 9031, seriesId: 3, label: 'Chapter 61', number: 61 }),
  makeManifest({ chapterId: 9032, seriesId: 3, label: 'Chapter 62', number: 62 }),
  makeManifest({ chapterId: 9051, seriesId: 5, label: 'Volume 2', number: 2 }),
]

const savedAt: Record<number, string> = { 1: daysAgo(3), 3: hoursAgo(5), 5: daysAgo(1) }

const records = new Map<number, OfflineChapter>(
  manifests.map((m) => [
    m.chapterId,
    {
      userId: 1,
      chapterId: m.chapterId,
      seriesId: m.seriesId,
      pageVersion: 'v1',
      pageCount: m.pageCount,
      bytes: 9_400_000,
      savedAt: savedAt[m.seriesId],
      reason: 'manual',
      state: 'saved',
    } satisfies OfflineChapter,
  ]),
)

const byId = new Map(manifests.map((m) => [m.chapterId, m]))
const savedManifests = http.get(api('/reader/chapter/:id'), ({ params }) => {
  const manifest = byId.get(Number(params.id))
  return manifest ? HttpResponse.json(manifest) : HttpResponse.json({ code: 'offline.unavailable' }, { status: 503 })
})

const meta = {
  component: OfflineHome,
  tags: ['ai-generated'],
  args: { supported: true, controlled: true, records, onRetry: fn() },
  parameters: { layout: 'padded', msw: [savedManifests] },
} satisfies Meta<typeof OfflineHome>

export default meta
type Story = StoryObj<typeof meta>

/** One card per series, opening the first saved chapter not finished; the half-read one leads. */
export const SavedChapters: Story = {
  play: async ({ canvas, args, userEvent }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: /saved on this device/i })).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('link', { name: /chapter|volume/i })).toHaveLength(3))
    const cards = canvas.getAllByRole('link', { name: /chapter|volume/i })
    await expect(cards[0]).toHaveAttribute('href', '/read/9012')
    await expect(cards[0]).toHaveTextContent('Resume')
    await expect(cards[0]).toHaveTextContent('3 saved')
    await userEvent.click(canvas.getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(args.onRetry).toHaveBeenCalled())
  },
}

export const NothingSaved: Story = {
  args: { records: new Map() },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: "You're offline" })).toBeVisible()
    await expect(await canvas.findByRole('link', { name: 'Offline reading' })).toBeVisible()
  },
}

/** Chapters are saved but no worker answers the tab (a hard reload, or the dev server): nothing can open. */
export const SavedButTabUncontrolled: Story = {
  args: { controlled: false },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/6 chapters are saved on this device/i)).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Reload' })).toBeVisible()
    await expect(canvas.queryByRole('heading', { level: 2, name: /saved on this device/i })).toBeNull()
  },
}

/** The worker is there but none of the saved manifests came back: say so rather than draw an empty rail. */
export const SavedButUnreadable: Story = {
  parameters: {
    msw: [http.get(api('/reader/chapter/:id'), () => HttpResponse.json({ code: 'offline.unavailable' }, { status: 503 }))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("The saved chapters wouldn't open")).toBeVisible()
  },
}

/** Plain HTTP: nothing can be saved, so there is nothing to point at either. */
export const OfflineUnsupported: Story = {
  args: { supported: false, records: new Map() },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/home comes back as soon as it does/i)).toBeVisible()
    await expect(canvas.queryByRole('link', { name: 'Offline reading' })).toBeNull()
  },
}

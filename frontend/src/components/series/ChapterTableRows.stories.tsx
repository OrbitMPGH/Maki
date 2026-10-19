import type { ComponentProps } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { Table, VisuallyHidden } from '@mantine/core'
import { chaptersFor, fileQuality, progressFor } from '../../../.storybook/mocks/series'
import { hoursAgo } from '../../../.storybook/mocks/api'
import { seriesMsw } from '../../../.storybook/mocks/series'
import type { ChapterDto, QueueItemDto } from '../../api/types'
import type { AnimeSpan } from '../../lib/animeCoverage'
import { ChapterRow, ChapterRowSkeletons, ChapterSpanRow, ChunkedRows, type ChapterRowActions } from './ChapterTableRows'

const chapters = chaptersFor(1, { missing: [4], specials: [3.5], upgradeable: 3, limit: 30 })
const progress = new Map(progressFor(1, chapters, 2).map((p) => [p.chapterId, p]))
const byNumber = (n: number): ChapterDto => chapters.find((c) => c.number === n)!

const actions: ChapterRowActions = {
  clickRow: fn(),
  toggleWanted: fn(),
  setRead: fn(),
  link: fn(),
  download: fn(),
  pickChapter: fn(),
  upgradeNow: fn(),
  setTrusted: fn(),
  deleteFile: fn(),
  saveOffline: fn(),
  removeOffline: fn(),
  toggleSpanFold: fn(),
  setMarkerRef: fn(),
}

const queueItem = (status: string, overrides: Partial<QueueItemDto> = {}): QueueItemDto => ({
  id: 1,
  chapterId: 1,
  seriesId: 1,
  seriesTitle: 'Lanterns Over Kasumi',
  releaseTitle: null,
  chapterTitle: null,
  chapterVolume: null,
  chapterNumber: '5',
  sourceName: 'mangadex',
  status,
  pagesTotal: 24,
  pagesDone: 9,
  retryCount: 0,
  nextAttempt: null,
  errorKey: null,
  errorParams: null,
  errorMessage: null,
  queuedAt: hoursAgo(1),
  completedAt: null,
  origin: 'manual',
  upgrade: null,
  ...overrides,
})

type RowProps = ComponentProps<typeof ChapterRow>

const baseRow: RowProps = {
  chapter: byNumber(5),
  progress: undefined,
  queueItem: undefined,
  selectMode: false,
  selected: false,
  hasAnimeMarkers: false,
  markers: undefined,
  foldedSpans: undefined,
  spanForMarker: () => undefined,
  canEditMetadata: true,
  canDownload: true,
  canDelete: true,
  canFindBetterCopy: true,
  canSaveOffline: false,
  offline: undefined,
  offlineSaving: undefined,
  wantedPending: false,
  readPending: false,
  upgradePending: false,
  upgradingThis: false,
  fileOrigin: (name) => ({ label: name === 'import' ? 'Imported' : 'MangaDex', scraped: name !== 'import', hint: '' }),
  renderLabel: (label) => (typeof label === 'string' ? label : (label.message ?? '')),
  actions,
}

const meta = {
  component: ChapterRow,
  tags: ['ai-generated'],
  args: baseRow,
  parameters: { layout: 'fullscreen', msw: seriesMsw() },
  render: (args) => (
    <Table>
      <Table.Thead>
        <Table.Tr>
          <Table.Th>Want</Table.Th>
          <Table.Th>Chapter</Table.Th>
          <Table.Th>Title</Table.Th>
          <Table.Th>Released</Table.Th>
          <Table.Th>Source</Table.Th>
          <Table.Th>Status</Table.Th>
          <Table.Th><VisuallyHidden>Actions</VisuallyHidden></Table.Th>
        </Table.Tr>
      </Table.Thead>
      <Table.Tbody>
        <ChapterRow {...args} />
      </Table.Tbody>
    </Table>
  ),
} satisfies Meta<typeof ChapterRow>

export default meta
type Story = StoryObj<typeof meta>

export const Downloaded: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText(/Ch\.5/)).toBeVisible()
    await expect(canvas.getByRole('switch', { name: /Want .*Ch\.5/ })).toBeChecked()
  },
}

export const ReadChapter: Story = {
  args: { chapter: byNumber(1), progress: progress.get(byNumber(1).id) },
}

export const InProgress: Story = {
  args: {
    chapter: byNumber(2),
    progress: { chapterId: byNumber(2).id, pageIndex: 11, pageCount: 24, completed: false, external: false, watched: false, unreadAt: null, updatedAt: hoursAgo(3) },
  },
}

export const WatchedFromAnime: Story = {
  args: {
    chapter: byNumber(2),
    progress: { chapterId: byNumber(2).id, pageIndex: 0, pageCount: 24, completed: true, external: false, watched: true, unreadAt: null, updatedAt: hoursAgo(3) },
  },
}

export const ReadInKavita: Story = {
  args: {
    chapter: byNumber(2),
    progress: { chapterId: byNumber(2).id, pageIndex: 0, pageCount: 0, completed: true, external: true, watched: false, unreadAt: null, updatedAt: hoursAgo(3) },
  },
}

export const MissingWanted: Story = {
  args: { chapter: byNumber(4) },
}

export const NotWanted: Story = {
  args: { chapter: { ...byNumber(6), wanted: false, hasFile: false, filePath: null, fileSourceName: null, fileQuality: null } },
}

export const Special: Story = {
  args: { chapter: byNumber(3.5) },
}

export const UpgradeAvailable: Story = {
  args: { chapter: { ...byNumber(1), fileQuality: fileQuality(11, { tier: 'aggregator', group: null, cutoffMet: false, score: 31 }) } },
}

export const ProtectedFile: Story = {
  args: { chapter: { ...byNumber(7), fileQuality: fileQuality(17, { trusted: true, cutoffMet: true, tier: 'official', group: null }) } },
}

export const InsideVolumeFile: Story = {
  args: { chapter: { ...byNumber(8), fileVolume: '1' } },
}

export const ImportedFile: Story = {
  args: { chapter: { ...byNumber(9), fileSourceName: 'import' } },
}

export const Queued: Story = {
  args: { chapter: byNumber(4), queueItem: queueItem('Queued') },
}

export const Downloading: Story = {
  args: { chapter: byNumber(4), queueItem: queueItem('Downloading') },
}

export const DownloadFailed: Story = {
  args: {
    chapter: byNumber(4),
    queueItem: queueItem('Failed', { errorMessage: 'The source returned a captcha page', retryCount: 3 }),
  },
}

export const SavedOffline: Story = {
  args: {
    canSaveOffline: true,
    offline: { userId: 1, chapterId: byNumber(5).id, seriesId: 1, pageVersion: 'v1', pageCount: 24, bytes: 21_000_000, savedAt: hoursAgo(5), reason: 'manual', state: 'saved' },
  },
}

export const SavingOffline: Story = {
  args: { canSaveOffline: true, offlineSaving: { done: 9, total: 24 } },
}

export const FileScheduledForDeletion: Story = {
  args: { chapter: { ...byNumber(1), fileDeleteDueAt: hoursAgo(-48) }, progress: progress.get(byNumber(1).id) },
}

export const SelectModeSelected: Story = {
  args: { selectMode: true, selected: true },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getByText(/Ch\.5/))
    await expect(args.actions.clickRow).toHaveBeenCalled()
  },
}

export const ReaderRow: Story = {
  args: { canEditMetadata: false, canDownload: false, canDelete: false, canFindBetterCopy: false },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('switch', { name: /Want .*Ch\.5/ })).toBeDisabled()
  },
}

export const RowActionsMenu: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const buttons = canvas.getAllByRole('button')
    await userEvent.click(buttons[buttons.length - 1])
    await expect(await within(canvasElement.ownerDocument.body).findByRole('menu')).toBeInTheDocument()
  },
}

const season: AnimeSpan = { key: 's1', label: 'S1', from: 1, to: 6, openEnded: false, lane: 0 }

export const FoldedSeason: StoryObj<typeof ChapterSpanRow> = {
  render: () => (
    <Table>
      <Table.Tbody>
        <ChapterSpanRow
          span={season}
          rows={chapters.slice(0, 6)}
          readProgress={progress}
          hasAnimeMarkers
          readTracking
          canEditMetadata
          wantedPending={false}
          actions={{ applyWanted: fn(), applyReadState: fn(), selectChapters: fn(), toggleSpanFold: fn() }}
        />
      </Table.Tbody>
    </Table>
  ),
}

export const FoldedSeasonMixedWanted: StoryObj<typeof ChapterSpanRow> = {
  render: () => (
    <Table>
      <Table.Tbody>
        <ChapterSpanRow
          span={{ ...season, label: 'Film + OVA', openEnded: true }}
          rows={chapters.slice(0, 6).map((c, i) => ({ ...c, wanted: i % 2 === 0 }))}
          readProgress={new Map()}
          hasAnimeMarkers
          readTracking={false}
          canEditMetadata={false}
          wantedPending={false}
          actions={{ applyWanted: fn(), applyReadState: fn(), selectChapters: fn(), toggleSpanFold: fn() }}
        />
      </Table.Tbody>
    </Table>
  ),
}

export const Skeletons: StoryObj<typeof ChapterRowSkeletons> = {
  render: () => (
    <Table>
      <Table.Tbody>
        <ChapterRowSkeletons count={6} />
      </Table.Tbody>
    </Table>
  ),
}

export const ChunkedFill: StoryObj<typeof ChunkedRows> = {
  render: () => (
    <Table>
      <Table.Tbody>
        <ChunkedRows rows={Array.from({ length: 250 }, (_, i) => i + 1)} chunk={100}>
          {(rows) => rows.map((n) => (
            <Table.Tr key={n}>
              <Table.Td>Row {n}</Table.Td>
            </Table.Tr>
          ))}
        </ChunkedRows>
      </Table.Tbody>
    </Table>
  ),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Row 250')).toBeVisible()
  },
}

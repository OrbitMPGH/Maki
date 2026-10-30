import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import {
  animeResume,
  archivePreview,
  archivedSeriesDto,
  hintedSeriesDto,
  providerDetail,
  seriesConversionRestorable,
  seriesDto,
  seriesMsw,
  torrentProposal,
} from '../../.storybook/mocks/series'
import SeriesDetailPage from './SeriesDetailPage'
import { monoText } from '../../.storybook/monoText'

const at = (id: number, query = '') => ({ router: { path: '/series/:id', initialEntries: [`/series/${id}${query}`] } })

const meta = {
  component: SeriesDetailPage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', ...at(1), msw: seriesMsw() },
} satisfies Meta<typeof SeriesDetailPage>

export default meta
type Story = StoryObj<typeof meta>

export const Ongoing: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: /Lanterns Over Kasumi/ })).toBeVisible()
    await expect(await canvas.findByText('Synopsis')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('tab', { name: /details/i })).toHaveAttribute('aria-selected', 'true'))
    await expect(await canvas.findByRole('heading', { name: 'Sources' })).toBeVisible()
  },
}

export const Completed: Story = {
  parameters: at(2),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: /The Ninth Kitchen/ })).toBeVisible()
    await expect(canvas.queryByText(/You've finished this/)).toBeNull()
  },
}

/** Completed and finished by every reader: a quiet line offers the archive and opens the same dialog as the menu. */
export const FinishedArchiveHint: Story = {
  parameters: { ...at(2), msw: seriesMsw(http.get(api('/series/:id'), () => HttpResponse.json(hintedSeriesDto(2)))) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByText("You've finished this. Archive it?")).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Archive' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(within(dialog).getByRole('button', { name: 'Archive' })).toBeVisible()
  },
}

let rated: number | null = null

/** Finished but unrated: the rate hint comes first, and rating from it hands the line over to the archive offer. */
export const FinishedRateHint: Story = {
  beforeEach: () => {
    rated = null
  },
  parameters: {
    ...at(2),
    msw: seriesMsw(
      http.get(api('/series/:id'), () =>
        HttpResponse.json({ ...hintedSeriesDto(2), rating: rated, rateHintEligible: rated == null }),
      ),
      http.put(api('/series/:id/rating'), async ({ request }) => {
        rated = ((await request.json()) as { rating: number | null }).rating
        return HttpResponse.json({ rating: rated })
      }),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText("You've finished this. How was it?")).toBeVisible()
    await expect(canvas.queryByText("You've finished this. Archive it?")).toBeNull()
    const stars = canvas.getByRole('group', { name: 'Rate this series' })
    // Mantine commits a rating from the star's label; the radio itself only previews it.
    await userEvent.click((within(stars).getByRole('radio', { name: '4' }) as HTMLInputElement).labels![0])
    await waitFor(() => expect(rated).toBe(8))
    await expect(await canvas.findByText("You've finished this. Archive it?")).toBeVisible()
  },
}

let hintDismissed = false

/** Dismissing posts once and the line goes away on the refetch. */
export const FinishedArchiveHintDismissed: Story = {
  beforeEach: () => {
    hintDismissed = false
  },
  parameters: {
    ...at(2),
    msw: seriesMsw(
      http.get(api('/series/:id'), () => HttpResponse.json({ ...hintedSeriesDto(2), archiveHintEligible: !hintDismissed })),
      http.post(api('/series/:id/archive-hint/dismiss'), () => {
        hintDismissed = true
        return new HttpResponse(null, { status: 204 })
      }),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Dismiss' }))
    await waitFor(() => expect(canvas.queryByText(/You've finished this/)).toBeNull())
    await expect(hintDismissed).toBe(true)
  },
}

export const Archived: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id'), () => HttpResponse.json(archivedSeriesDto(1)))) },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('heading', { name: /Lanterns Over Kasumi/ })).toBeVisible()
    await expect(await canvas.findByText(/Archived on .*\. Maki is not checking sources for this series\./)).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Restore to library' })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Remove files' })).toBeVisible()
    const download = await canvas.findByRole('button', { name: /^Download .* wanted$/ })
    await waitFor(() => expect(download).toHaveAttribute('aria-disabled', 'true'))
    await userEvent.click(download)
    await expect(canvas.queryByRole('dialog')).toBeNull()
    const releases = await canvas.findByRole('button', { name: 'Search releases' })
    await waitFor(() => expect(releases).toHaveAttribute('aria-disabled', 'true'))
    await userEvent.click(releases)
    await expect(canvas.queryByRole('dialog')).toBeNull()
  },
}

export const ArchivedRestoreAsksAboutFiles: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id'), () =>
        HttpResponse.json({ ...archivedSeriesDto(1), chapterFileCount: 0, removedChapterCount: 142 }),
      ),
      archivePreview({ redownloadableChapters: 142 }),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Restore to library' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByText('Download the 142 removed chapters again?')).toBeVisible()
  },
}

/** The restore went through but the download could not be queued: the toast says why. */
export const ArchivedRestoreQueueError: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id'), () =>
        HttpResponse.json({ ...archivedSeriesDto(1), chapterFileCount: 0, removedChapterCount: 142 }),
      ),
      archivePreview({ redownloadableChapters: 142 }),
      http.post(api('/series/:id/restore'), () => HttpResponse.json({ queued: 0, queueError: 'Downloads are paused' })),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Restore to library' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Restore and download' }))
    await expect(
      await within(canvasElement.ownerDocument.body).findByText(/the removed chapters were not queued: Downloads are paused/),
    ).toBeVisible()
  },
}

export const Hiatus: Story = {
  parameters: at(4),
}

export const Cancelled: Story = {
  parameters: at(12),
}

export const ManyUnreadChapters: Story = {
  parameters: at(3, '?tab=chapters'),
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Chapters' })).toBeVisible()
    await waitFor(() => expect(canvas.getAllByText(/Ch\.\d+/).length).toBeGreaterThan(5))
  },
}

export const ChaptersTab: Story = {
  parameters: at(1, '?tab=chapters'),
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('heading', { name: 'Chapters' })).toBeVisible()
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Search chapters' }), '52')
    await waitFor(() => expect(canvas.getAllByText(/52/).length).toBeGreaterThan(0))
  },
}

export const ChapterFilterMissing: Story = {
  parameters: at(1, '?tab=chapters'),
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('radio', { name: /^Missing/ }))
    await expect(await canvas.findByText(/Ch\.17\b/)).toBeVisible()
  },
}

type PlayContext = Parameters<NonNullable<Story['play']>>[0]

const selectThreeChapters = async ({ canvas, userEvent }: PlayContext) => {
  await userEvent.click(await canvas.findByRole('button', { name: 'Select' }))
  const rows = await canvas.findAllByRole('row', { selected: false })
  for (const row of rows.slice(0, 3)) await userEvent.click(row)
  await expect(await canvas.findByText('3 selected')).toBeVisible()
}

export const SelectModeBulkBar: Story = {
  parameters: at(1, '?tab=chapters'),
  play: async (ctx) => {
    await selectThreeChapters(ctx)
    await waitFor(() => expect(ctx.canvas.getByRole('button', { name: 'Download' })).toBeEnabled())
    await waitFor(() => expect(ctx.canvas.getByRole('button', { name: 'More' })).toBeEnabled())
    await expect(ctx.canvas.queryByRole('button', { name: 'Unlink' })).toBeNull()
  },
}

export const SelectModeMoreMenu: Story = {
  parameters: at(1, '?tab=chapters'),
  play: async (ctx) => {
    await selectThreeChapters(ctx)
    await ctx.userEvent.click(ctx.canvas.getByRole('button', { name: 'More' }))
    const page = body(ctx.canvasElement)
    await expect(await page.findByRole('menuitem', { name: "Don't want" })).toBeVisible()
    await expect(page.getByRole('menuitem', { name: 'Link to file' })).toBeVisible()
    await expect(page.getByRole('menuitem', { name: 'Unlink' })).toBeVisible()
  },
}

export const SelectModeDeleteMenu: Story = {
  parameters: at(1, '?tab=chapters'),
  play: async (ctx) => {
    await selectThreeChapters(ctx)
    await ctx.userEvent.click(ctx.canvas.getByRole('button', { name: 'Delete' }))
    const page = body(ctx.canvasElement)
    await expect(await page.findByRole('menuitem', { name: 'Delete files' })).toBeVisible()
    await ctx.userEvent.click(page.getByRole('menuitem', { name: 'Remove chapters' }))
    await expect(await page.findByRole('dialog', { name: 'Remove chapters?' })).toBeVisible()
  },
}

export const SelectModePhone: Story = {
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  parameters: { viewport: { defaultViewport: 'mobile1' }, ...at(1, '?tab=chapters') },
  play: async (ctx) => {
    await selectThreeChapters(ctx)
    await ctx.userEvent.click(ctx.canvas.getByRole('button', { name: 'Actions' }))
    await expect(await body(ctx.canvasElement).findByRole('menuitem', { name: 'Download' })).toBeVisible()
  },
}

export const FilesTab: Story = {
  parameters: at(1, '?tab=files'),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(monoText(/Bonus booklet/))).toBeVisible()
  },
}

export const SourceMatchPending: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id'), () => HttpResponse.json(seriesDto(5, { sourceMatchPending: true }))),
      http.get(api('/sourcemapping'), () => HttpResponse.json([])),
    ),
    ...at(5),
  },
}

export const NoSourcesLinked: Story = {
  parameters: {
    msw: seriesMsw(http.get(api('/sourcemapping'), () => HttpResponse.json([]))),
    ...at(5),
  },
}

export const ImportLinkPending: Story = {
  parameters: {
    msw: seriesMsw(http.get(api('/series/:id'), () => HttpResponse.json(seriesDto(8, { importLinkPending: true })))),
    ...at(8),
  },
}

export const WithAnimeAdaptation: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id'), () =>
        HttpResponse.json(
          seriesDto(1, {
            hasAnime: true,
            animeName: 'Lanterns Over Kasumi',
            animeStart: 'Vol 1, Chap 1 (S1) / Vol 5, Chap 39 (S2)',
            animeEnd: 'Vol 5, Chap 38 (S1) / Vol 9, Chap 79 (S2)',
          }),
        ),
      ),
      http.get(api('/series/:id/anime-resume'), () => HttpResponse.json(animeResume)),
      http.get(api('/recommendations/detail/:id'), () => HttpResponse.json(providerDetail(1, { hasAnime: true }))),
    ),
    ...at(1),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Anime coverage')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: /read ch\. 39/i })).toBeVisible()
  },
}

export const PendingTorrentProposal: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id'), () => HttpResponse.json(seriesDto(1, { pendingProposalId: 77 }))),
      http.get(api('/upgrades/proposals'), () => HttpResponse.json([torrentProposal(1)])),
    ),
    ...at(1, '?tab=chapters'),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Grab' })).toBeVisible()
  },
}

export const NumberingClash: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id'), () => HttpResponse.json(seriesDto(6, { numberingClash: 'comick|asurascans' }))),
    ),
    ...at(6),
  },
}

export const DownloadsInProgress: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id'), () => HttpResponse.json(seriesDto(3, { queuedCount: 4, downloadingCount: 2 }))),
    ),
    ...at(3),
  },
}

export const Loading: Story = {
  parameters: {
    msw: seriesMsw(http.get(api('/series/:id'), () => neverResolve())),
  },
}

export const NotFound: Story = {
  parameters: at(999),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Series not found')).toBeVisible()
    await expect(await canvas.findByRole('link', { name: /back to library/i })).toBeVisible()
  },
}

/** Offline, and this device holds no copy of the series: not gone, just out of reach. */
export const OfflineNotSaved: Story = {
  parameters: {
    msw: seriesMsw(http.get(api('/series/:id'), () => failWith(503, 'Not available offline', 'offline.unavailable'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 1, name: 'Not available offline' })).toBeVisible()
    await expect(canvas.queryByText('Series not found')).toBeNull()
  },
}

export const ServerError: Story = {
  parameters: {
    msw: seriesMsw(http.get(api('/series/:id'), () => failWith(500))),
  },
}

export const ChaptersFailedToLoad: Story = {
  parameters: {
    msw: seriesMsw(http.get(api('/chapter'), () => failWith(500))),
    ...at(1, '?tab=chapters'),
  },
}

export const NoChaptersYet: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id'), () =>
        HttpResponse.json(seriesDto(9, { wantedChapterCount: 0, knownChapterCount: 0, chapterFileCount: 0, readChapterCount: null })),
      ),
      http.get(api('/chapter'), () => HttpResponse.json([])),
      http.get(api('/reader/series/:id/progress'), () => HttpResponse.json([])),
    ),
    ...at(9, '?tab=chapters'),
  },
}

export const Reader: Story = {
  parameters: { auth: 'reader', ...at(1) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: /Lanterns Over Kasumi/ })).toBeVisible()
    await expect(await canvas.findByText('Synopsis')).toBeVisible()
    await expect(canvas.queryByRole('heading', { name: 'Sources' })).toBeNull()
  },
}

export const ReaderChapters: Story = {
  parameters: { auth: 'reader', ...at(3, '?tab=chapters') },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Chapters' })).toBeVisible()
  },
}

export const Phone: Story = {
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  parameters: { viewport: { defaultViewport: 'mobile1' }, ...at(1, '?tab=chapters') },
}

const body = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

export const ActionsMenu: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('heading', { name: /Lanterns Over Kasumi/ })
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await expect(await body(canvasElement).findByRole('menuitem', { name: 'Edit metadata' })).toBeVisible()
  },
}

export const EditMetadataDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Edit metadata' }))
    await waitFor(() => expect(body(canvasElement).getByRole('textbox', { name: /title/i })).toHaveValue('Lanterns Over Kasumi'))
  },
}

export const MoveSeriesDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Move to another root folder' }))
    await expect(await body(canvasElement).findByRole('dialog', { name: 'Move series' })).toBeVisible()
  },
}

export const RenameFilesDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Rename files' }))
    await expect(await body(canvasElement).findByRole('dialog', { name: 'Rename files' })).toBeVisible()
  },
}

export const UndoConversionDialog: Story = {
  parameters: {
    msw: seriesMsw(http.get(api('/series/:id/pageconversion/history'), () => HttpResponse.json(seriesConversionRestorable))),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('heading', { name: /Lanterns Over Kasumi/ })
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Undo conversion...' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Undo conversion' })
    await expect(await within(dialog).findByText(/48 files have their original in the trash/)).toBeVisible()
  },
}

export const UndoConversionAbsentWithoutRestorableFiles: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('heading', { name: /Lanterns Over Kasumi/ })
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    await expect(await within(menu).findByRole('menuitem', { name: /Page conversion/ })).toBeVisible()
    await expect(within(menu).queryByRole('menuitem', { name: /Undo conversion/ })).toBeNull()
  },
}

export const RemoveSeriesDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Remove from library' }))
    await expect(await body(canvasElement).findByRole('checkbox', { name: /also delete files/i })).toBeVisible()
  },
}

export const SearchReleasesDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Search releases' }))
    await expect(await body(canvasElement).findByRole('dialog', { name: 'Search releases' })).toBeVisible()
  },
}

export const ReaderRequestsChapters: Story = {
  parameters: { auth: 'reader', ...at(3, '?tab=chapters') },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Request chapters' }))
    await expect(await body(canvasElement).findByRole('dialog', { name: /Request chapters of Iron Petal Academy/ })).toBeVisible()
  },
}

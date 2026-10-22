import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { animeResume, providerDetail, seriesDto, seriesMsw, torrentProposal } from '../../.storybook/mocks/series'
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

export const SelectModeBulkBar: Story = {
  parameters: at(1, '?tab=chapters'),
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Select' }))
    await expect(await canvas.findByText(/selected/i)).toBeVisible()
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

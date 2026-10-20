import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve, readerMe, signedInAs } from '../../.storybook/mocks/api'
import { addSeriesHandlers, requestOnlyHandlers } from '../../.storybook/mocks/entry'
import { catalogue } from '../../.storybook/mocks/shared'
import { catalogueDetail, catalogueItem } from '../../.storybook/mocks/entry/fixtures'
import { notifications } from '@mantine/notifications'
import { clearTabState } from '../lib/pageState'
import type { RecommendationIndexStatus } from '../api/hooks'
import AddSeriesPage from './AddSeriesPage'

const meta = {
  component: AddSeriesPage,
  tags: ['ai-generated'],
  parameters: {
    layout: 'padded',
    router: { path: '/add', initialEntries: ['/add'] },
    msw: addSeriesHandlers,
  },
  // Search text, filters and the view mode are remembered per tab and per browser, so one story's
  // typing or list view would otherwise open the next one already applied.
  beforeEach: () => {
    clearTabState()
    // The notification store is module-wide, so a toast from the previous story would still be up.
    notifications.clean()
    for (const key of Object.keys(localStorage)) {
      if (key.startsWith('discover-')) localStorage.removeItem(key)
    }
  },
} satisfies Meta<typeof AddSeriesPage>

export default meta
type Story = StoryObj<typeof meta>

const at = (url: string) => ({ router: { path: '/add', initialEntries: [url] } })

/** The detail card is a Mantine modal, so it lives on document.body, outside the canvas. */
const modalOf = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

export const BrowsingTheCatalogue: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Add series' })).toBeVisible()
    await expect(await canvas.findByText('Browsing the catalogue')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' })).toBeVisible()
    // Titles already in the library read "View" and link through instead of offering an add.
    await expect(await canvas.findByRole('button', { name: 'View Lanterns Over Kasumi' })).toBeVisible()
  },
}

export const SearchResults: Story = {
  parameters: at('/add?q=Lanterns'),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('1 match', {}, { timeout: 5000 })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'View Lanterns Over Kasumi' })).toBeVisible()
  },
}

export const SearchByTyping: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('Browsing the catalogue')
    await userEvent.type(await canvas.findByPlaceholderText('Search by title, description, or by feel'), 'tea')
    await expect(await canvas.findByRole('button', { name: 'View and add Clockwork Tea House' }, { timeout: 5000 })).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear search' }))
    await expect(await canvas.findByText('Browsing the catalogue')).toBeVisible()
  },
}

export const NoResults: Story = {
  parameters: at('/add?q=zzzzzz'),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No matches', {}, { timeout: 5000 })).toBeVisible()
  },
}

export const CorrectedSpelling: Story = {
  parameters: {
    ...at('/add?q=lantrens'),
    msw: [
      http.post(api('/recommendations/discover/search'), () =>
        HttpResponse.json({
          mode: 'title',
          correctedQuery: 'lanterns',
          items: [],
          fallback: null,
        }),
      ),
      ...addSeriesHandlers,
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('lanterns', { selector: 'strong' }, { timeout: 5000 })).toBeVisible()
  },
}

export const CreatorMatched: Story = {
  parameters: {
    ...at('/add?q=Aoi Mizuhara'),
    msw: [
      http.post(api('/recommendations/discover/search'), () =>
        HttpResponse.json({
          mode: 'title',
          items: [
            catalogueItemFor(1),
            catalogueItemFor(10),
          ],
          credits: [{ name: 'Aoi Mizuhara', roles: ['author', 'artist'], workCount: 2 }],
          fallback: null,
        }),
      ),
      ...addSeriesHandlers,
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: /Aoi Mizuhara \(2\)/ }, { timeout: 5000 })).toBeVisible()
  },
}

export const MeaningSearchUnavailable: Story = {
  parameters: {
    ...at('/add?q=lonely lighthouse keeper'),
    msw: [
      http.post(api('/recommendations/discover/search'), () =>
        HttpResponse.json({ mode: 'title', items: [catalogueItemFor(9)], fallback: 'unavailable' }),
      ),
      http.get(api('/settings/recommendations'), () =>
        HttpResponse.json({
          modelPresent: false,
          dumpPresent: true,
          vectorCount: 0,
          recommendableTotal: null,
          running: false,
          phase: 'idle',
          embedded: 0,
          scanned: 0,
          startedAt: null,
          finishedAt: null,
          lastEmbedded: 0,
          lastError: null,
          estimatedSecondsRemaining: null,
          prebuiltEnabled: true,
          prebuiltInstalledAt: null,
          embeddingModel: 'base',
          useFullDump: false,
          modelSwitching: false,
          modelSwitchError: null,
        } satisfies RecommendationIndexStatus),
      ),
      ...addSeriesHandlers,
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/title match only/, {}, { timeout: 5000 })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Download index' })).toBeVisible()
  },
}

export const MeaningSearchUnavailableForReader: Story = {
  parameters: {
    ...at('/add?q=lonely lighthouse keeper'),
    auth: 'reader',
    msw: [
      http.post(api('/recommendations/discover/search'), () =>
        HttpResponse.json({ mode: 'title', items: [catalogueItemFor(9)], fallback: 'unavailable' }),
      ),
      ...requestOnlyHandlers,
      ...addSeriesHandlers,
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/an admin can set up search by meaning/, {}, { timeout: 5000 })).toBeVisible()
  },
}

export const FiltersOpen: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByText('Browsing the catalogue')
    await userEvent.click(await canvas.findByRole('button', { name: 'Filters' }))
    await expect(await canvas.findByRole('button', { name: /Reset/i })).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: {
    msw: [http.post(api('/recommendations/discover/feed'), () => neverResolve()), ...addSeriesHandlers],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Add series' })).toBeVisible()
    await expect(canvas.queryByText('Nothing here')).not.toBeInTheDocument()
  },
}

export const EmptyCatalogue: Story = {
  parameters: {
    msw: [http.post(api('/recommendations/discover/feed'), () => HttpResponse.json([])), ...addSeriesHandlers],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing here')).toBeVisible()
  },
}

export const BrowseFails: Story = {
  parameters: {
    msw: [
      http.post(api('/recommendations/discover/feed'), () => failWith(500, 'The catalogue index is unavailable')),
      ...addSeriesHandlers,
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The catalogue index is unavailable')).toBeVisible()
  },
}

export const DetailWithAddForm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' }))
    const body = modalOf(canvasElement)
    await expect(await body.findByRole('dialog')).toBeVisible()
    await waitFor(() => expect(body.getByRole('button', { name: 'Add' })).toBeEnabled())
    await waitFor(() => expect(body.getByLabelText('Root folder', { selector: 'input' })).toHaveValue('/library/manga'))
  },
}

export const AddingASeries: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' }))
    const body = modalOf(canvasElement)
    const add = await body.findByRole('button', { name: 'Add' })
    await waitFor(() => expect(add).toBeEnabled())
    await userEvent.click(add)
    await expect(await body.findByRole('button', { name: 'Go to series' })).toBeVisible()
    await expect(await body.findByText('Added Iron Petal Academy')).toBeVisible()
  },
}

export const AddFails: Story = {
  parameters: {
    msw: [
      http.post(api('/series'), () => failWith(409, 'That folder is not writable', 'error.series.folderNotWritable')),
      ...addSeriesHandlers,
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' }))
    const body = modalOf(canvasElement)
    const add = await body.findByRole('button', { name: 'Add' })
    await waitFor(() => expect(add).toBeEnabled())
    await userEvent.click(add)
    // The failure toast belongs to the app's own query client; here the button just stays an Add.
    await waitFor(() => expect(body.getByRole('button', { name: 'Add' })).toBeEnabled())
    await expect(body.queryByRole('button', { name: 'Go to series' })).not.toBeInTheDocument()
  },
}

export const DetailForOwnedSeries: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View Lanterns Over Kasumi' }))
    const body = modalOf(canvasElement)
    await expect(await body.findByText('In your library')).toBeVisible()
    await expect(await body.findByRole('button', { name: 'View in library' })).toBeVisible()
  },
}

export const DetailWithoutRootFolders: Story = {
  parameters: {
    msw: [http.get(api('/rootfolder'), () => HttpResponse.json([])), ...addSeriesHandlers],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' }))
    const body = modalOf(canvasElement)
    await waitFor(() => expect(body.getByRole('button', { name: 'Add' })).toBeDisabled())
  },
}

export const AddWithoutAGrantedRootFolder: Story = {
  // A non-admin who may add, with no folder granted yet. beforeEach, because the preview's own
  // sign-in handler is applied after `parameters.msw` and would win over one given there.
  beforeEach: ({ msw }) => {
    msw.use(signedInAs({ ...readerMe, permissions: 'AddSeries', permissionNames: ['AddSeries'] }))
  },
  parameters: {
    msw: [http.get(api('/rootfolder'), () => HttpResponse.json([])), ...addSeriesHandlers],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' }))
    const body = modalOf(canvasElement)
    const notice = await body.findByText(/no root folder has been granted to you yet/)
    await waitFor(() => expect(notice).toBeVisible())
  },
}

export const AddFormForAnimeAdaptedTitle: Story = {
  parameters: {
    msw: [
      http.get(api('/recommendations/detail/:id'), () =>
        HttpResponse.json({
          ...detailFor(3),
          hasAnime: true,
          animeStart: 'Vol 1, Chap 1 (S1)',
          animeEnd: 'Vol 6, Chap 52 (S1)',
          animeResume: {
            animeTitle: 'Iron Petal Academy',
            services: ['AniList'],
            score: 8,
            basis: 'allSeasons',
            coveredTo: 52,
            resumeAt: 53,
            coveredLabel: 'Season 1',
            nextLabel: null,
            nextFrom: null,
            nextTo: null,
            inLibrarySeriesId: null,
          },
        }),
      ),
      ...addSeriesHandlers,
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' }))
    const body = modalOf(canvasElement)
    await expect(await body.findByText('Mark ch. 1 to 52 watched')).toBeVisible()
  },
}

export const ListView: Story = {
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' })
    await userEvent.click(await canvas.findByLabelText('List view'))
    await expect(await canvas.findByRole('button', { name: /^Iron Petal Academy/ })).toBeVisible()
  },
}

export const Mobile: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' })).toBeVisible()
  },
}

// The reader view: no AddSeries, so the same page asks an admin instead of adding.

export const RequestInsteadOfAdd: Story = {
  parameters: { auth: 'reader', msw: [...requestOnlyHandlers, ...addSeriesHandlers] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Request series' })).toBeVisible()
    await expect(
      await canvas.findByText('Browse or search MangaBaka and ask an admin for a title. You can ask for a chapter range too.'),
    ).toBeVisible()
  },
}

export const RequestingATitle: Story = {
  parameters: { auth: 'reader', msw: [http.get(api('/requests'), () => HttpResponse.json([])), ...addSeriesHandlers] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' }))
    const body = modalOf(canvasElement)
    await expect(await body.findByText('Ask for this')).toBeVisible()
    await userEvent.type(await body.findByLabelText('From'), '1')
    await userEvent.type(await body.findByLabelText('To'), '20')
    await userEvent.click(await body.findByRole('button', { name: 'Request series' }))
    await expect(await body.findByText('Requested Iron Petal Academy')).toBeVisible()
    await expect(await body.findByText('An admin decides where it lands and what gets downloaded.')).toBeVisible()
  },
}

export const RequestAlreadyPending: Story = {
  parameters: { auth: 'reader', msw: [...requestOnlyHandlers, ...addSeriesHandlers] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    // Grave Robber of the Seventh Hall (id 9) has a pending request in the fixture.
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Grave Robber of the Seventh Hall' }))
    const body = modalOf(canvasElement)
    await expect(await body.findByText('Requested')).toBeVisible()
  },
}

export const RequestWasRejected: Story = {
  parameters: { auth: 'reader', msw: [...requestOnlyHandlers, ...addSeriesHandlers] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Neon Shrine Maiden' }))
    const body = modalOf(canvasElement)
    await expect(await body.findByText('Your last request was rejected: Already coming through another title.')).toBeVisible()
    await expect(await body.findByRole('button', { name: 'Request series' })).toBeVisible()
  },
}

export const RequestFails: Story = {
  parameters: {
    auth: 'reader',
    msw: [
      http.get(api('/requests'), () => HttpResponse.json([])),
      http.post(api('/requests'), () => failWith(409, 'You already asked for this title')),
      ...addSeriesHandlers,
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'View and add Iron Petal Academy' }))
    const body = modalOf(canvasElement)
    await userEvent.click(await body.findByRole('button', { name: 'Request series' }))
    await waitFor(() => expect(body.getByRole('button', { name: 'Request series' })).toBeEnabled())
    await expect(body.queryByText('Requested Iron Petal Academy')).not.toBeInTheDocument()
  },
}

const catalogueItemFor = (id: number) => catalogueItem(catalogue[id - 1])
const detailFor = (id: number) => catalogueDetail(catalogue[id - 1])

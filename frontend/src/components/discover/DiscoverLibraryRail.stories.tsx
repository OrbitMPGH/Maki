import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../../.storybook/mocks/api'
import {
  ANIME_PROVIDER_ID,
  catalogueItem,
  detailFor,
  discoverPage,
  resetTabState,
} from '../../../.storybook/mocks/discover'
import { DiscoverLibraryRail } from './DiscoverLibraryRail'

const item = catalogueItem(0)

const meta = {
  component: DiscoverLibraryRail,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  args: {
    item,
    detail: detailFor(item.providerId),
    inLibrarySeriesId: null,
    rootFolders: [
      { id: 1, path: '/library/manga', freeSpace: 412_000_000_000, accessible: true },
      { id: 2, path: '/library/manhwa', freeSpace: 96_000_000_000, accessible: true },
    ],
    onClose: fn(),
    addedFrom: 'recommendation',
  },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 340 }}>
        <Story />
      </div>
    ),
  ],
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverLibraryRail>

export default meta
type Story = StoryObj<typeof meta>

export const AdminAdd: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Add' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText(/Added The Salt Cartographer/)).toBeVisible()
  },
}

export const AddFails: Story = {
  parameters: {
    msw: discoverPage(http.post(api('/series'), () => failWith(409, 'That series is already in the library'))),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Add' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Add' })).toBeEnabled())
  },
}

export const InLibrary: Story = {
  args: { inLibrarySeriesId: 1 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'View in library' })).toBeVisible()
  },
}

export const WithAnimeWatched: Story = {
  args: { item: catalogueItem(6), detail: detailFor(ANIME_PROVIDER_ID) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Mark ch. 1 to 22 watched')).toBeVisible()
  },
}

export const NoRootFolderGranted: Story = {
  args: { rootFolders: [] },
  parameters: { auth: 'reader', msw: discoverPage() },
}

export const ReaderRequest: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await expect(await canvas.findByText('Ask for this')).toBeVisible()
    await userEvent.type(await canvas.findByLabelText('Note (optional)'), 'Would love this one')
    await userEvent.click(canvas.getByRole('button', { name: 'Request series' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText(/Requested The Salt Cartographer/)).toBeVisible()
  },
}

export const AlreadyRequested: Story = {
  parameters: {
    auth: 'reader',
    msw: discoverPage(
      http.get(api('/requests'), () =>
        HttpResponse.json([
          {
            id: 700,
            kind: 'NewSeries',
            status: 'Pending',
            userId: 2,
            requestedBy: 'reader',
            metadataProviderId: item.providerId,
            seriesId: null,
            title: item.title,
            coverUrl: null,
            year: 2023,
            chapterStart: null,
            chapterEnd: null,
            note: null,
            created: '2026-10-18T09:00:00Z',
            resolvedAt: null,
            resolvedBy: null,
            resolutionNote: null,
            queuedCount: null,
            editedAt: null,
            editedBy: null,
            originalChapterStart: null,
            originalChapterEnd: null,
          },
        ]),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Requested')).toBeVisible()
  },
}

export const RequestRejected: Story = {
  parameters: {
    auth: 'reader',
    msw: discoverPage(
      http.get(api('/requests'), () =>
        HttpResponse.json([
          {
            id: 690,
            kind: 'NewSeries',
            status: 'Rejected',
            userId: 2,
            requestedBy: 'reader',
            metadataProviderId: item.providerId,
            seriesId: null,
            title: item.title,
            coverUrl: null,
            year: 2023,
            chapterStart: null,
            chapterEnd: null,
            note: null,
            created: '2026-10-10T09:00:00Z',
            resolvedAt: '2026-10-11T09:00:00Z',
            resolvedBy: 'maki',
            resolutionNote: 'Already covered by another source',
            queuedCount: null,
            editedAt: null,
            editedBy: null,
            originalChapterStart: null,
            originalChapterEnd: null,
          },
        ]),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Your last request was rejected/)).toBeVisible()
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import {
  catalogueItem,
  discoverPage,
  ownedItem,
  resetTabState,
} from '../../../.storybook/mocks/discover'
import { detailFor } from '../../../.storybook/mocks/discover/fixtures'
import { shortlistMock } from '../../../.storybook/mocks/shortlist'
import { DiscoverDetailModal } from './DiscoverDetailModal'

const meta = {
  component: DiscoverDetailModal,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  args: {
    item: catalogueItem(0),
    inLibrarySeriesId: null,
    rootFolders: [
      { id: 1, path: '/library/manga', freeSpace: 412_000_000_000, accessible: true },
      { id: 2, path: '/library/manhwa', freeSpace: 96_000_000_000, accessible: true },
    ],
    onClose: fn(),
    feedbackContext: { surface: 'browse' },
  },
  parameters: { layout: 'fullscreen', msw: discoverPage() },
} satisfies Meta<typeof DiscoverDetailModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogOf = (el: HTMLElement) => within(el.ownerDocument.body).findByRole('dialog')

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByRole('heading', { name: 'The Salt Cartographer' })).toBeVisible()
    await expect(await within(dialog).findByText('Synopsis')).toBeVisible()
    await expect(await within(dialog).findByText('At a glance')).toBeVisible()
    await expect(await within(dialog).findByText('quietpageturner')).toBeVisible()
  },
}

const LONG_TITLE =
  "I Was Reborn as the Lighthouse Keeper's Overly Honest Cartographer, and Now the Whole Salt Coast Wants Me to Redraw Every Border Before the Tide Comes In"

/** A light-novel length title: capped at three lines so the band leaves the body its room. */
export const LongTitle: Story = {
  args: { item: { ...catalogueItem(0), title: LONG_TITLE } },
  parameters: {
    msw: discoverPage(
      http.get(api('/recommendations/detail/:id'), ({ params }) =>
        HttpResponse.json({ ...detailFor(String(params.id)), title: LONG_TITLE }),
      ),
    ),
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    const heading = await within(dialog).findByRole('heading', { name: LONG_TITLE })
    await expect(heading).toHaveAttribute('data-length', 'long')
    await expect(await within(dialog).findByRole('img', { name: /^Score / })).toBeVisible()
    await expect(await within(dialog).findByRole('button', { name: 'Add to library' })).toBeVisible()
  },
}

export const Closed: Story = {
  args: { item: null },
}

export const Loading: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/recommendations/detail/:id'), () => neverResolve()),
    ),
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByRole('heading', { name: 'The Salt Cartographer' })).toBeVisible()
  },
}

export const DetailFailed: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/detail/:id'), () => failWith(500, 'The catalogue is unavailable'))),
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText("Couldn't load the details for this title.")).toBeVisible()
    await expect(await within(dialog).findByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

export const InLibrary: Story = {
  args: { item: ownedItem(1), inLibrarySeriesId: 1 },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByRole('button', { name: 'View in library' })).toBeVisible()
    await expect(within(dialog).queryByRole('button', { name: 'Preview' })).toBeNull()
  },
}

export const WithAnimeAdaptation: Story = {
  args: { item: catalogueItem(6) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText('Anime coverage')).toBeVisible()
    await expect(await within(dialog).findByText('Mark ch. 1 to 22 watched')).toBeVisible()
  },
}

export const ReaderCohortHint: Story = {
  args: { item: catalogueItem(3) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    const badge = await within(dialog).findByText('Higher for readers like you')
    await expect(badge).toBeVisible()
    await userEvent.hover(badge)
    await expect(
      await within(document.body).findByText(
        '412 AniList readers with reading habits like yours scored this 0.7 higher than AniList readers overall.',
      ),
    ).toBeVisible()
  },
}

export const NoSynopsisOrReviews: Story = {
  args: { item: catalogueItem(14, { description: null }) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText('The catalogue has no synopsis for this one.')).toBeVisible()
  },
}

export const WithRerollDice: Story = {
  args: { onReroll: fn() },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByRole('button', { name: 'Roll again' })).toBeVisible()
  },
}

export const OpenedOutsideRecommendations: Story = {
  args: { feedbackContext: undefined },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByRole('button', { name: 'Thumbs up' })).toBeVisible()
    await expect(await within(dialog).findByRole('button', { name: 'Thumbs down' })).toBeVisible()
  },
}

export const ReaderAsksForTitle: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText('Ask for this')).toBeVisible()
  },
}

export const OpenPreviewReader: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Preview' }))
    await expect(await within(canvasElement.ownerDocument.body).findByRole('button', { name: 'Close preview' })).toBeVisible()
  },
}

export const CloseButton: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Close' }))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

const modalShortlist = shortlistMock([])

export const ShortlistFromTheModal: Story = {
  args: { savedFrom: { savedFrom: 'rail:trending' } },
  beforeEach: () => modalShortlist.reset(),
  parameters: { msw: discoverPage(...modalShortlist.handlers) },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Shortlist' }))
    await expect(await within(dialog).findByRole('button', { name: 'Shortlisted' })).toHaveAttribute('aria-pressed', 'true')
    await expect(await within(dialog).findByRole('textbox', { name: 'Note for later' })).toBeVisible()
    await waitFor(() => expect(modalShortlist.snapshot().map((e) => e.savedFrom)).toEqual(['rail:trending']))
  },
}

export const ShortlistHiddenWhenOwned: Story = {
  args: { item: ownedItem(3), inLibrarySeriesId: 3 },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByRole('heading', { level: 1 })).toBeVisible()
    await expect(within(dialog).queryByRole('button', { name: 'Shortlist' })).toBeNull()
  },
}

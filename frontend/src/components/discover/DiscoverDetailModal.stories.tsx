import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import {
  catalogueItem,
  discoverPage,
  ownedItem,
  resetTabState,
} from '../../../.storybook/mocks/discover'
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
    await expect(within(dialog).queryByRole('button', { name: 'Preview first chapter' })).toBeNull()
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
    await expect(await within(dialog).findByText('Higher for readers like you')).toBeVisible()
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
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Preview first chapter' }))
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

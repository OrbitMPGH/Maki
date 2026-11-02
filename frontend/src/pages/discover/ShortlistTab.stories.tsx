import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { discoverPage, resetTabState } from '../../../.storybook/mocks/discover'
import {
  changedEntry,
  hiatusEntry,
  removedEntry,
  shortlistEntries,
  shortlistMock,
  shortlistNoDumpHandlers,
} from '../../../.storybook/mocks/shortlist'
import { ShortlistTab } from './ShortlistTab'

const filled = shortlistMock(shortlistEntries)
const empty = shortlistMock([])
const requester = shortlistMock(shortlistEntries)

const meta = {
  component: ShortlistTab,
  tags: ['ai-generated'],
  beforeEach: () => {
    resetTabState()
    for (const mock of [filled, empty, requester]) mock.reset()
  },
  parameters: { layout: 'padded', msw: discoverPage(...filled.handlers) },
} satisfies Meta<typeof ShortlistTab>

export default meta
type Story = StoryObj<typeof meta>

export const Filled: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Changed since you saved them' })).toBeVisible()
    await expect(await canvas.findByRole('heading', { name: 'Waiting for you' })).toBeVisible()
    await expect(await canvas.findByText('Completed since you saved it')).toBeVisible()
    await expect(await canvas.findByText('+12 chapters')).toBeVisible()
    await expect(await canvas.findByText('Wait for the ending before starting.')).toBeVisible()
    await expect(await canvas.findByText('Saved 2 months ago from Trending now')).toBeVisible()
    await expect(await canvas.findByText(/^Saved .* from Currently popular$/)).toBeVisible()
    await expect(await canvas.findByText('2 titles have waited more than six months.')).toBeVisible()
    await expect((await canvas.findAllByText(removedEntry.title)).length).toBeGreaterThan(0)
    await expect(await canvas.findByRole('button', { name: `Remove ${removedEntry.title} from your shortlist` })).toBeVisible()
    await expect(await canvas.findByText('Manhwa')).toBeVisible()
  },
}

export const HasAnAnime: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Has an anime/ }))
    await waitFor(() => expect(canvas.queryByText(hiatusEntry.title)).toBeNull())
    await expect(await canvas.findByText(changedEntry.title)).toBeVisible()
  },
}

export const InTheLibrary: Story = {
  play: async ({ canvas }) => {
    const rows = await canvas.findAllByText('In the library')
    await waitFor(() => expect(rows.length).toBeGreaterThanOrEqual(2))
    await expect(await canvas.findByRole('link', { name: 'Open series' })).toHaveAttribute('href', '/series/3')
  },
}

export const GotIt: Story = {
  play: async ({ canvas, userEvent }) => {
    const buttons = await canvas.findAllByRole('button', { name: 'Got it' })
    await userEvent.click(buttons[0])
    await waitFor(() => expect(canvas.queryByText('Completed since you saved it')).toBeNull())
  },
}

export const ChangedFilter: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Changed since saved/ }))
    await waitFor(() => expect(canvas.queryByRole('heading', { name: 'Waiting for you' })).toBeNull())
    await expect(await canvas.findByRole('heading', { name: 'Changed since you saved them' })).toBeVisible()
  },
}

export const ShowTheOldest: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Show the oldest' }))
    await waitFor(() => expect(canvas.queryByText(/have waited more than six months/)).toBeNull())
  },
}

export const EditNote: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Edit note' }))[0])
    const input = await canvas.findByRole('textbox', { name: 'Note for later' })
    await userEvent.clear(input)
    await userEvent.type(input, 'Start in spring{Enter}')
    await expect(await canvas.findByText('Start in spring')).toBeVisible()
  },
}

export const RemoveWithUndo: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: `Remove ${changedEntry.title} from your shortlist` }))
    await waitFor(() => expect(canvas.queryByText(changedEntry.title)).toBeNull())
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('button', { name: 'Undo' }))
    await expect(await canvas.findByText(changedEntry.title)).toBeVisible()
  },
}

export const Empty: Story = {
  parameters: { msw: discoverPage(...empty.handlers) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing on your shortlist')).toBeVisible()
    const toggle = await canvas.findByRole('button', { name: 'How the shortlist differs from your library' })
    await expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await expect(await canvas.findByRole('link', { name: 'Browse the catalogue' })).toBeVisible()
  },
}

export const EmptyDisclosureOpen: Story = {
  parameters: { msw: discoverPage(...empty.handlers) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'How the shortlist differs from your library' }))
    await expect(await canvas.findByRole('rowheader', { name: 'Stats, Rewind, XP' })).toBeVisible()
  },
}

export const NoDump: Story = {
  parameters: { msw: discoverPage(...shortlistNoDumpHandlers) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Discover's catalogue is not downloaded yet")).toBeVisible()
    await expect(canvas.queryByText('Nothing on your shortlist')).toBeNull()
  },
}

export const Requester: Story = {
  parameters: {
    auth: 'reader',
    msw: discoverPage(
      http.get(api('/requests'), () =>
        HttpResponse.json([
          {
            id: 5,
            kind: 'NewSeries',
            status: 'Pending',
            userId: 2,
            requestedBy: 'reader',
            metadataProviderId: String(shortlistEntries[2].item?.providerId),
            seriesId: null,
            title: shortlistEntries[2].item?.title,
            coverUrl: null,
            year: null,
            chapterStart: null,
            chapterEnd: null,
            note: null,
            created: new Date().toISOString(),
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
      ...requester.handlers,
    ),
  },
  play: async ({ canvas }) => {
    await expect((await canvas.findAllByRole('button', { name: 'Request' })).length).toBeGreaterThan(0)
    await expect(await canvas.findByText('Requested')).toBeVisible()
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'Add to library' })).toBeNull())
  },
}

export const Loading: Story = {
  parameters: { msw: discoverPage(http.get(api('/shortlist'), () => neverResolve())) },
}

export const LoadError: Story = {
  parameters: { msw: discoverPage(http.get(api('/shortlist'), () => failWith(500, 'The shortlist could not be read'))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Could not load your shortlist')).toBeVisible()
  },
}

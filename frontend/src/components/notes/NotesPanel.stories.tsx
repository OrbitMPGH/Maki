import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { adminMe, api, failWith, neverResolve, signedInAs } from '../../../.storybook/mocks/api'
import { NOTES_CHAPTER, statefulNotes } from '../../../.storybook/mocks/notes'
import { layerHandlers } from '../../../.storybook/mocks/shell/use'
import NotesPanel from './NotesPanel'

const stateful = layerHandlers(() => statefulNotes())

const meta = {
  component: NotesPanel,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    opened: true,
    onClose: fn(),
    chapterId: NOTES_CHAPTER,
    seriesId: 1,
    chapterLabel: 'Chapter 132',
    pageCount: 18,
  },
} satisfies Meta<typeof NotesPanel>

export default meta
type Story = StoryObj<typeof meta>

const panel = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

export const SeriesPage: Story = {
  play: async ({ canvasElement }) => {
    const body = panel(canvasElement)
    await expect(await body.findByRole('dialog', { name: 'Notes, Chapter 132' })).toBeVisible()
    await expect(await body.findByText(/tea cups/)).toBeVisible()
    await expect(await body.findByText('Note deleted')).toBeVisible()
    await expect(body.queryByRole('radio', { name: 'This page' })).toBeNull()
  },
}

export const UnfinishedChapterFoldsOthers: Story = {
  args: { spoilerGuard: true },
  play: async ({ canvasElement, userEvent }) => {
    const body = panel(canvasElement)
    await expect(await body.findByText(/tea cups/)).toBeVisible()
    await waitFor(() => expect(body.getAllByText('Tap to show').length).toBeGreaterThan(0))
    await expect(body.queryByText(/Best fight so far/)).toBeNull()
    await userEvent.click((await body.findAllByRole('button', { name: /^Rin/ }))[1])
    await expect(await body.findByText(/Best fight so far/)).toBeVisible()
  },
}

export const InReaderThisPage: Story = {
  args: { inReader: true, page: 11, pinPage: 11, onGoToPage: fn() },
  play: async ({ canvasElement }) => {
    const body = panel(canvasElement)
    await expect(await body.findByText(/Best fight so far/)).toBeVisible()
    await waitFor(() => expect(body.getByRole('radio', { name: 'This page' })).toBeChecked())
    await expect(body.queryByText(/tea cups/)).toBeNull()
    await expect(await body.findByRole('checkbox', { name: 'Pin to page 12' })).toBeChecked()
  },
}

export const InReaderPageWithoutNotes: Story = {
  args: { inReader: true, page: 4, pinPage: 4 },
  play: async ({ canvasElement, userEvent }) => {
    const body = panel(canvasElement)
    await waitFor(() => expect(body.getByRole('radio', { name: 'Whole chapter' })).toBeChecked())
    await userEvent.click(await body.findByText('This page'))
    await expect(await body.findByText('No notes on this page.')).toBeVisible()
    await userEvent.click(await body.findByRole('button', { name: 'Show the whole chapter' }))
    await expect(await body.findByText(/tea cups/)).toBeVisible()
  },
}

export const OpenedOnAThread: Story = {
  args: { initialThreadId: 3 },
  play: async ({ canvasElement }) => {
    const body = panel(canvasElement)
    await expect(await body.findByText(/last line reads differently/)).toBeVisible()
    await waitFor(() => {
      const thread = canvasElement.ownerDocument.getElementById('note-thread-3')
      expect(thread).toHaveAttribute('data-highlight')
    })
  },
}

export const AddingANote: Story = {
  beforeEach: stateful,
  args: { inReader: true, page: 6, pinPage: 6 },
  play: async ({ canvasElement, userEvent }) => {
    const body = panel(canvasElement)
    await userEvent.type(await body.findByRole('textbox', { name: 'Write a note' }), 'Look at the window')
    await userEvent.click(await body.findByRole('button', { name: 'Add note' }))
    await expect(await body.findByText('Look at the window')).toBeVisible()
    await waitFor(() => expect(body.getByRole('textbox', { name: 'Write a note' })).toHaveValue(''))
  },
}

export const AddingAnUnpinnedNoteLeavesThePageFilter: Story = {
  beforeEach: stateful,
  args: { inReader: true, page: 6, pinPage: 6 },
  play: async ({ canvasElement, userEvent }) => {
    const body = panel(canvasElement)
    await userEvent.type(await body.findByRole('textbox', { name: 'Write a note' }), 'About the whole chapter')
    await userEvent.click(await body.findByRole('checkbox', { name: 'Pin to page 7' }))
    await userEvent.click(await body.findByRole('button', { name: 'Add note' }))
    await expect(await body.findByText('About the whole chapter')).toBeVisible()
    await expect(body.getByRole('radio', { name: 'Whole chapter' })).toBeChecked()
  },
}

export const NoNotesYet: Story = {
  parameters: {
    msw: [http.get(api('/chapters/:id/notes'), () => HttpResponse.json([]))],
  },
  play: async ({ canvasElement }) => {
    await expect(await panel(canvasElement).findByText('No notes on this chapter yet.')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: {
    msw: [http.get(api('/chapters/:id/notes'), neverResolve)],
  },
  play: async ({ canvasElement }) => {
    await expect(await panel(canvasElement).findByRole('dialog')).toBeVisible()
  },
}

export const OfflineNeedsAConnection: Story = {
  parameters: {
    msw: [
      http.get(api('/chapters/:id/notes'), () =>
        HttpResponse.json({ code: 'offline.unavailable', error: 'Not available offline' }, { status: 503 }),
      ),
    ],
  },
  play: async ({ canvasElement }) => {
    const body = panel(canvasElement)
    await expect(await body.findByText('Notes need a connection')).toBeVisible()
    await expect(await body.findByRole('textbox', { name: 'Write a note' })).toBeDisabled()
  },
}

export const FailsToLoad: Story = {
  parameters: {
    msw: [http.get(api('/chapters/:id/notes'), () => failWith(500))],
  },
  play: async ({ canvasElement }) => {
    await expect(await panel(canvasElement).findByText('Could not load the notes.')).toBeVisible()
  },
}

export const SingleUserMode: Story = {
  parameters: { msw: [signedInAs({ ...adminMe, singleUser: true })] },
  args: { chapterId: 5 },
  play: async ({ canvasElement }) => {
    const body = panel(canvasElement)
    await expect(await body.findByRole('textbox', { name: 'Write a note' })).toBeVisible()
    await waitFor(() => expect(body.queryByRole('radio', { name: 'Shared' })).toBeNull())
  },
}

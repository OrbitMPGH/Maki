import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { readerNotes } from '../../../.storybook/mocks/notes'
import NoteThread, { type NoteThreadActions } from './NoteThread'

const actions: NoteThreadActions = {
  reply: fn(async () => {}),
  update: fn(async () => {}),
  remove: fn(async () => {}),
}

const byId = (id: number) => readerNotes.find((n) => n.id === id)!

const meta = {
  component: NoteThread,
  tags: ['ai-generated'],
  args: { note: byId(2), actions, pageCount: 18 },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 420, padding: 16 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof NoteThread>

export default meta
type Story = StoryObj<typeof meta>

export const SharedThread: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('Rin')).toBeVisible()
    await expect(await canvas.findByText('Another reader')).toBeVisible()
    await expect(await canvas.findByText('Page 12')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Reply' }))
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Write a reply' }), 'Same here')
    await userEvent.click(await canvas.findByRole('button', { name: 'Reply' }))
    await waitFor(() => expect(args.actions.reply).toHaveBeenCalledWith(2, 'Same here'))
  },
}

export const OwnPrivateNote: Story = {
  args: { note: byId(1) },
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('You')).toBeVisible()
    await expect(await canvas.findByText('Private')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Reply' })).toBeNull()
    await userEvent.click(await canvas.findByRole('button', { name: 'Delete' }))
    await expect(await canvas.findByText('Delete this note?')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Delete' }))
    await waitFor(() => expect(args.actions.remove).toHaveBeenCalledWith(1))
  },
}

export const EditingOwnNote: Story = {
  args: { note: byId(1) },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Edit' }))
    const box = await canvas.findByRole('textbox', { name: 'Write a note' })
    await userEvent.clear(box)
    await userEvent.type(box, 'Rewritten')
    await userEvent.click(await canvas.findByRole('button', { name: 'Save' }))
    await waitFor(() => expect(args.actions.update).toHaveBeenCalledWith(1, { body: 'Rewritten' }))
  },
}

export const Tombstone: Story = {
  args: { note: byId(7) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Note deleted')).toBeVisible()
    await expect(await canvas.findByText('I wrote this one down before it went.')).toBeVisible()
  },
}

export const RemovedByAdmin: Story = {
  args: { note: { ...byId(7), deleted: 'moderator' } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Removed by an admin')).toBeVisible()
  },
}

export const DeletedAccount: Story = {
  args: { note: { ...byId(2), author: null, replies: [] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Deleted account')).toBeVisible()
  },
}

export const FoldedUntilFinished: Story = {
  args: { fold: true },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('Tap to show')).toBeVisible()
    await expect(canvas.queryByText(/Best fight so far/)).toBeNull()
    await userEvent.click(await canvas.findByRole('button', { name: /Rin/ }))
    await expect(await canvas.findByText(/Best fight so far/)).toBeVisible()
  },
}

export const FoldSparesOwnNotes: Story = {
  args: { note: byId(1), fold: true },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/tea cups/)).toBeVisible()
    await expect(canvas.queryByText('Tap to show')).toBeNull()
  },
}

export const PageChipJumps: Story = {
  args: { onGoToPage: fn() },
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Go to page 12' }))
    await waitFor(() => expect(args.onGoToPage).toHaveBeenCalledWith(11))
  },
}

export const PagePastTheEndIsWholeChapter: Story = {
  args: { pageCount: 8 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Rin')).toBeVisible()
    await expect(canvas.queryByText('Page 12')).toBeNull()
  },
}

export const ActionFails: Story = {
  args: {
    note: byId(1),
    actions: {
      ...actions,
      remove: fn(async () => {
        throw new Error('That note is already gone.')
      }),
    },
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Delete' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Delete' }))
    await expect(await canvas.findByText('That note is already gone.')).toBeVisible()
  },
}

export const Offline: Story = {
  args: { readOnly: true },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Reply' })).toBeDisabled()
  },
}

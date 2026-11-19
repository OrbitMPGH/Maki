import type { Meta, StoryObj } from '@storybook/react-vite'
import { http, HttpResponse } from 'msw'
import { expect, waitFor, within } from 'storybook/test'
import { api } from '../../../.storybook/mocks/api'
import { KeyboardShortcutsSection } from './KeyboardShortcutsSection'

const meta = {
  component: KeyboardShortcutsSection,
  tags: ['ai-generated'],
} satisfies Meta<typeof KeyboardShortcutsSection>

export default meta
type Story = StoryObj<typeof meta>

type Canvas = ReturnType<typeof within>

// The controls stay disabled until the saved bindings arrive.
async function addKey(canvas: Canvas, label: string) {
  const button = await canvas.findByRole('button', { name: `Add a key to ${label}` })
  await waitFor(() => expect(button).toBeEnabled())
  return button
}

const recorder = (canvas: Canvas, label: string) =>
  canvas.findByRole('button', { name: new RegExp(`^Press a key, Esc cancels.*${label}`) })

const row = (canvas: Canvas, label: string) =>
  canvas.getByText(label, { selector: '.shortcut-label' }).closest('li') as HTMLElement

type User = { click: (element: Element) => Promise<void> }

// The list opens in a modal from the card; everything after this queries inside it.
async function openList(canvas: Canvas, userEvent: User) {
  const edit = await canvas.findByRole('button', { name: 'Edit shortcuts' })
  await waitFor(() => expect(edit).toBeEnabled())
  await userEvent.click(edit)
  return within(await within(document.body).findByRole('dialog', { name: 'Keyboard shortcuts' }))
}

export const Defaults: Story = {
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('Every key is the default')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Reset all' })).toBeNull()
    const list = await openList(canvas, userEvent)
    await list.findByText('Next page', { selector: '.shortcut-label' })
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('Forward arrow'))
    await expect(row(list, 'Open search')).toHaveTextContent('Ctrl + K')
    const columns = document.querySelector('.shortcut-columns')!
    await expect(columns.children).toHaveLength(2)
    await expect(getComputedStyle(columns).gridTemplateColumns.split(' ')).toHaveLength(2)
    await expect(list.getByText("Forward and back arrows follow the series' reading direction.")).toBeVisible()
    await expect(list.queryByRole('button', { name: 'Reset all' })).toBeNull()
  },
}

export const RecordsAKey: Story = {
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await userEvent.click(await addKey(list, 'Next page'))
    await expect(await recorder(list, 'Next page')).toHaveFocus()
    await userEvent.keyboard('j')
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('J'))
    await waitFor(() => expect(list.getByRole('status')).toHaveTextContent('J added to Next page'))
    await waitFor(() => expect(list.getByRole('button', { name: 'Add a key to Next page' })).toHaveFocus())
    await expect(list.getByRole('button', { name: 'Reset all' })).toBeVisible()
  },
}

export const KeepsShiftOnLetters: Story = {
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await userEvent.click(await addKey(list, 'Full screen'))
    await recorder(list, 'Full screen')
    await userEvent.keyboard('{Shift>}b{/Shift}')
    await waitFor(() => expect(row(list, 'Full screen')).toHaveTextContent('Shift + B'))
    // Plain B stays where it was.
    await expect(row(list, 'Bookmark this page')).toHaveTextContent('B')
    await expect(row(list, 'Bookmark this page')).not.toHaveTextContent('No key')
  },
}

export const MovesAKeyInUse: Story = {
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await userEvent.click(await addKey(list, 'Page thumbnails'))
    await recorder(list, 'Page thumbnails')
    await userEvent.keyboard('b')
    await waitFor(() => expect(row(list, 'Page thumbnails')).toHaveTextContent('B'))
    await waitFor(() => expect(row(list, 'Bookmark this page')).toHaveTextContent('No key'))
  },
}

export const ResettingARowTakesItsKeyBack: Story = {
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await userEvent.click(await addKey(list, 'Page thumbnails'))
    await recorder(list, 'Page thumbnails')
    await userEvent.keyboard('b')
    await waitFor(() => expect(row(list, 'Bookmark this page')).toHaveTextContent('No key'))
    await userEvent.click(list.getByRole('button', { name: 'Reset Bookmark this page to its default keys' }))
    await waitFor(() => expect(row(list, 'Bookmark this page')).toHaveTextContent('B'))
    await waitFor(() => expect(row(list, 'Page thumbnails')).not.toHaveTextContent('B'))
  },
}

export const ArrowsOnPageTurnsFollowTheDirection: Story = {
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await addKey(list, 'Next page')
    await userEvent.click(list.getByRole('button', { name: 'Remove Forward arrow from Next page' }))
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('No key'))
    await userEvent.click(await addKey(list, 'Next page'))
    await recorder(list, 'Next page')
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('Forward arrow'))
  },
}

export const RefusesASideArrowElsewhere: Story = {
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await userEvent.click(await addKey(list, 'Zoom in'))
    await recorder(list, 'Zoom in')
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(list.getByRole('status')).toHaveTextContent('The left and right arrows turn pages.'))
    await expect(row(list, 'Zoom in')).not.toHaveTextContent('→')
    await expect(row(list, 'Next page')).toHaveTextContent('Forward arrow')
  },
}

export const RemovingKeepsFocusInTheRow: Story = {
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await addKey(list, 'Full screen')
    await userEvent.click(list.getByRole('button', { name: 'Remove F from Full screen' }))
    await waitFor(() => expect(row(list, 'Full screen')).toHaveTextContent('No key'))
    await waitFor(() => expect(list.getByRole('button', { name: 'Add a key to Full screen' })).toHaveFocus())
  },
}

export const ResetAllOffersUndo: Story = {
  parameters: {
    msw: [http.get(api('/settings/shortcuts'), () => HttpResponse.json({ bindings: { 'reader.next': ['j'] } }))],
  },
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('J'))
    await userEvent.click(await list.findByRole('button', { name: 'Reset all' }))
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('Forward arrow'))
    // The footer's Reset all turns into Undo reset, and focus stays on it.
    await waitFor(() => expect(list.getByRole('button', { name: 'Undo reset' })).toHaveFocus())
    const body = within(document.body)
    await userEvent.click(await body.findByRole('button', { name: 'Undo' }))
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('J'))
  },
}

/** Inside the modal, Undo for Reset all sits in its footer, where Tab can reach it. */
export const UndoResetInTheModal: Story = {
  parameters: {
    msw: [http.get(api('/settings/shortcuts'), () => HttpResponse.json({ bindings: { 'reader.next': ['j'] } }))],
  },
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('J'))
    await userEvent.click(await list.findByRole('button', { name: 'Reset all' }))
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('Forward arrow'))
    await userEvent.click(await list.findByRole('button', { name: 'Undo reset' }))
    await waitFor(() => expect(row(list, 'Next page')).toHaveTextContent('J'))
    await waitFor(() => expect(list.queryByRole('button', { name: 'Undo reset' })).toBeNull())
  },
}

export const EscapeCancels: Story = {
  play: async ({ canvas, userEvent }) => {
    const list = await openList(canvas, userEvent)
    await userEvent.click(await addKey(list, 'Full screen'))
    await recorder(list, 'Full screen')
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(list.queryByRole('button', { name: /^Press a key, Esc cancels/ })).toBeNull())
    // The list stays open: Esc only cancelled the recording.
    await expect(within(document.body).getByRole('dialog', { name: 'Keyboard shortcuts' })).toBeVisible()
    await expect(row(list, 'Full screen')).toHaveTextContent('F')
  },
}

export const LoadFailed: Story = {
  parameters: {
    msw: [http.get(api('/settings/shortcuts'), () => HttpResponse.json({ error: 'Down' }, { status: 500 }))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Couldn't load your shortcuts.")).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Edit shortcuts' })).toBeNull()
  },
}

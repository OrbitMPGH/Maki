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

const recorder = (canvas: Canvas, label: string) => canvas.findByRole('button', { name: new RegExp(`^Press a key for ${label}`) })

const row = (canvas: Canvas, label: string) =>
  canvas.getByText(label, { selector: '.shortcut-label' }).closest('li') as HTMLElement

export const Defaults: Story = {
  play: async ({ canvas }) => {
    await canvas.findByText('Next page', { selector: '.shortcut-label' })
    await waitFor(() => expect(row(canvas, 'Next page')).toHaveTextContent('Forward arrow'))
    await expect(row(canvas, 'Open search')).toHaveTextContent('Ctrl + K')
    await expect(canvas.getByText("Forward and back arrows follow the series' reading direction.")).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Reset all' })).toBeNull()
  },
}

export const RecordsAKey: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await addKey(canvas, 'Next page'))
    await expect(await recorder(canvas, 'Next page')).toHaveFocus()
    await userEvent.keyboard('j')
    await waitFor(() => expect(row(canvas, 'Next page')).toHaveTextContent('J'))
    await waitFor(() => expect(canvas.getByRole('status')).toHaveTextContent('J added to Next page'))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Add a key to Next page' })).toHaveFocus())
    await expect(canvas.getByRole('button', { name: 'Reset all' })).toBeVisible()
  },
}

export const KeepsShiftOnLetters: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await addKey(canvas, 'Full screen'))
    await recorder(canvas, 'Full screen')
    await userEvent.keyboard('{Shift>}b{/Shift}')
    await waitFor(() => expect(row(canvas, 'Full screen')).toHaveTextContent('Shift + B'))
    // Plain B stays where it was.
    await expect(row(canvas, 'Bookmark this page')).toHaveTextContent('B')
    await expect(row(canvas, 'Bookmark this page')).not.toHaveTextContent('No key')
  },
}

export const MovesAKeyInUse: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await addKey(canvas, 'Page thumbnails'))
    await recorder(canvas, 'Page thumbnails')
    await userEvent.keyboard('b')
    await waitFor(() => expect(row(canvas, 'Page thumbnails')).toHaveTextContent('B'))
    await waitFor(() => expect(row(canvas, 'Bookmark this page')).toHaveTextContent('No key'))
  },
}

export const ResettingARowTakesItsKeyBack: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await addKey(canvas, 'Page thumbnails'))
    await recorder(canvas, 'Page thumbnails')
    await userEvent.keyboard('b')
    await waitFor(() => expect(row(canvas, 'Bookmark this page')).toHaveTextContent('No key'))
    await userEvent.click(canvas.getByRole('button', { name: 'Reset Bookmark this page to its default keys' }))
    await waitFor(() => expect(row(canvas, 'Bookmark this page')).toHaveTextContent('B'))
    await waitFor(() => expect(row(canvas, 'Page thumbnails')).not.toHaveTextContent('B'))
  },
}

export const ArrowsOnPageTurnsFollowTheDirection: Story = {
  play: async ({ canvas, userEvent }) => {
    await addKey(canvas, 'Next page')
    await userEvent.click(canvas.getByRole('button', { name: 'Remove Forward arrow from Next page' }))
    await waitFor(() => expect(row(canvas, 'Next page')).toHaveTextContent('No key'))
    await userEvent.click(await addKey(canvas, 'Next page'))
    await recorder(canvas, 'Next page')
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(row(canvas, 'Next page')).toHaveTextContent('Forward arrow'))
  },
}

export const RefusesASideArrowElsewhere: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await addKey(canvas, 'Zoom in'))
    await recorder(canvas, 'Zoom in')
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(canvas.getByRole('status')).toHaveTextContent('The left and right arrows turn pages.'))
    await expect(row(canvas, 'Zoom in')).not.toHaveTextContent('→')
    await expect(row(canvas, 'Next page')).toHaveTextContent('Forward arrow')
  },
}

export const RemovingKeepsFocusInTheRow: Story = {
  play: async ({ canvas, userEvent }) => {
    await addKey(canvas, 'Full screen')
    await userEvent.click(canvas.getByRole('button', { name: 'Remove F from Full screen' }))
    await waitFor(() => expect(row(canvas, 'Full screen')).toHaveTextContent('No key'))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Add a key to Full screen' })).toHaveFocus())
  },
}

export const ResetAllOffersUndo: Story = {
  parameters: {
    msw: [http.get(api('/settings/shortcuts'), () => HttpResponse.json({ bindings: { 'reader.next': ['j'] } }))],
  },
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(row(canvas, 'Next page')).toHaveTextContent('J'))
    await userEvent.click(await canvas.findByRole('button', { name: 'Reset all' }))
    await waitFor(() => expect(row(canvas, 'Next page')).toHaveTextContent('Forward arrow'))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Add a key to Next page' })).toHaveFocus())
    const body = within(document.body)
    await userEvent.click(await body.findByRole('button', { name: 'Undo' }))
    await waitFor(() => expect(row(canvas, 'Next page')).toHaveTextContent('J'))
  },
}

export const EscapeCancels: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await addKey(canvas, 'Full screen'))
    await recorder(canvas, 'Full screen')
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(canvas.queryByRole('button', { name: /^Press a key for Full screen/ })).toBeNull())
    await expect(row(canvas, 'Full screen')).toHaveTextContent('F')
  },
}

export const LoadFailed: Story = {
  parameters: {
    msw: [http.get(api('/settings/shortcuts'), () => HttpResponse.json({ error: 'Down' }, { status: 500 }))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Couldn't load your shortcuts.")).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Add a key to Next page' })).toBeDisabled()
  },
}

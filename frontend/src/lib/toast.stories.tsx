import type { Meta, StoryObj } from '@storybook/react-vite'
import { Button, Group, Text } from '@mantine/core'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { notifications } from './toast'

/** Every toast in the app goes through lib/toast: Sonner's behaviour, Mantine's Notification look. */
const meta: Meta = {
  title: 'Lib/Toasts',
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  render: () => (
    <Group gap="xs">
      <Button variant="default" onClick={() => notifications.show({ message: 'Saved', color: 'var(--ok)' })}>
        Success
      </Button>
      <Button
        variant="default"
        onClick={() => notifications.show({ message: 'Could not reach the server.', color: 'var(--danger)' })}
      >
        Error
      </Button>
      <Button
        variant="default"
        onClick={() => notifications.show({ message: 'MangaDex is rate limiting requests.', color: 'var(--warn)' })}
      >
        Warning
      </Button>
      <Button variant="default" onClick={() => notifications.show({ message: 'Library scan started.' })}>
        Info
      </Button>
    </Group>
  ),
}

export default meta
type Story = StoryObj

const body = () => within(document.body)

export const Tones: Story = {
  play: async ({ canvas }) => {
    for (const name of ['Success', 'Error', 'Warning', 'Info']) {
      await userEvent.click(await canvas.findByRole('button', { name }))
    }
    await body().findByText('Library scan started.')
    await waitFor(() => expect(body().getAllByRole('alert')).toHaveLength(4))
  },
}

export const WithTitle: Story = {
  play: async () => {
    notifications.show({
      title: 'First chapter ready',
      message: 'Chapter 1 of Kitchen Witch Diaries is ready to read.',
      color: 'var(--ok)',
      autoClose: false,
    })
    await body().findByText('First chapter ready')
  },
}

export const UndoAction: Story = {
  play: async () => {
    const id = notifications.show({
      autoClose: 8000,
      message: (
        <Group gap="xs" wrap="wrap">
          <Text size="sm">Hidden from recommendations.</Text>
          <Button size="xs" variant="subtle" onClick={() => notifications.show({ message: 'Restored.', color: 'var(--ok)' })}>
            Undo
          </Button>
        </Group>
      ),
    })
    await userEvent.click(await body().findByRole('button', { name: 'Undo' }))
    await body().findByText('Restored.')
    notifications.hide(id)
  },
}

/** A loading toast updated in place by id, as the metadata download and long jobs do. */
export const LoadingThenDone: Story = {
  play: async () => {
    notifications.show({ id: 'job', loading: true, autoClose: false, message: 'Rebuilding missing images…' })
    await body().findByText('Rebuilding missing images…')
    notifications.update({ id: 'job', color: 'var(--ok)', message: 'Rebuilt 12 images.' })
    await body().findByText('Rebuilt 12 images.')
    await waitFor(() => expect(body().queryByText('Rebuilding missing images…')).toBeNull())
  },
}

export const ClosedByButton: Story = {
  play: async () => {
    let closed = false
    notifications.show({ message: 'Close me', autoClose: false, onClose: () => { closed = true } })
    const toast = await body().findByRole('alert')
    await userEvent.click(within(toast).getByRole('button'))
    await waitFor(() => expect(body().queryByText('Close me')).toBeNull())
    await waitFor(() => expect(closed).toBe(true))
  },
}

/** Four at once collapse into a deck; hovering the stack spreads it out. */
export const Stacked: Story = {
  play: async () => {
    notifications.show({ message: 'Added Kitchen Witch Diaries.', color: 'var(--ok)', autoClose: false })
    notifications.show({ message: 'Searching 4 sources…', loading: true, autoClose: false })
    notifications.show({ message: 'Could not reach WeebCentral.', color: 'var(--danger)', autoClose: false })
    notifications.show({ message: 'Queued 12 chapters.', color: 'var(--ok)', autoClose: false })
    await body().findByText('Queued 12 chapters.')
  },
}

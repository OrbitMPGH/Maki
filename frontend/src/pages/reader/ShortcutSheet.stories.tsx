import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import ShortcutSheet from './ShortcutSheet'

const meta = {
  component: ShortcutSheet,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: { rtl: true, onClose: fn() },
  decorators: [
    (Story) => (
      <div className="reader-root">
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ShortcutSheet>

export default meta
type Story = StoryObj<typeof meta>

export const RightToLeft: Story = {
  play: async ({ canvas }) => {
    const dialog = await canvas.findByRole('dialog', { name: 'Keyboard shortcuts' })
    await waitFor(() => expect(dialog).toBeVisible())
    // Reading right to left, the left arrow turns the page forward.
    await waitFor(() => expect(canvas.getByText('Next page').closest('div')).toHaveTextContent('←'))
  },
}

export const LeftToRight: Story = {
  args: { rtl: false },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByText('Next page').closest('div')).toHaveTextContent('→'))
  },
}

export const ListsTheBookmarkKeys: Story = {
  play: async ({ canvas }) => {
    const previous = (await canvas.findByText('Previous bookmark')).closest('div')
    await expect(previous).toHaveTextContent(',')
    await expect(canvas.getByText('Next bookmark').closest('div')).toHaveTextContent('.')
    await expect(canvas.getByText('Leave the reader')).toBeVisible()
  },
}

export const ShowsReboundKeys: Story = {
  parameters: {
    msw: [
      http.get(api('/settings/shortcuts'), () =>
        HttpResponse.json({ bindings: { 'reader.next': ['j'], 'reader.bookmark': [] } }),
      ),
    ],
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByText('Next page').closest('div')).toHaveTextContent('J'))
    await expect(canvas.getByText('Next page').closest('div')).not.toHaveTextContent('←')
    await expect(canvas.getByText('Bookmark this page').closest('div')).toHaveTextContent('No key')
  },
}

export const ClosesFromTheButton: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Close' }))
    await expect(args.onClose).toHaveBeenCalledOnce()
  },
}

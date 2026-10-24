import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { IconArrowLeft, IconPlus } from '@tabler/icons-react'
import { EmptyState } from './EmptyState'

const meta = {
  component: EmptyState,
  tags: ['ai-generated'],
} satisfies Meta<typeof EmptyState>

export default meta
type Story = StoryObj<typeof meta>

export const Inline: Story = {
  args: {
    title: 'Nothing in the queue',
    description: 'Downloads you start, and new chapters Maki finds, show up here.',
  },
}

export const Compact: Story = {
  args: { title: 'No failed downloads', compact: true, mood: 'pleased' },
}

export const WithMood: Story = {
  args: {
    title: 'No requests yet',
    description: 'Ask for a series and it waits here until an admin decides.',
    mood: 'asleep',
    actionLabel: 'Request a series',
    actionTo: '/add',
  },
}

export const EmptyLibrary: Story = {
  args: {
    art: 'shelf',
    headingOrder: 1,
    title: 'Your library is empty',
    description: 'Add a series to start tracking new chapters.',
    actionLabel: 'Add series',
    actionTo: '/add',
    actionIcon: <IconPlus size={16} />,
    secondaryActionLabel: 'Import a folder',
    secondaryActionTo: '/import',
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 1, name: /library is empty/i })).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('link', { name: /add series/i })).toHaveAttribute('href', '/add'))
    await waitFor(() => expect(canvas.getByRole('link', { name: /import a folder/i })).toHaveAttribute('href', '/import'))
  },
}

export const NotFound: Story = {
  args: {
    art: 'missing',
    headingOrder: 1,
    code: '/series/4040',
    title: 'Page not found',
    description: 'Nothing lives at this address. The link may be old or mistyped.',
    actionLabel: 'Go to start page',
    actionTo: '/',
    actionIcon: <IconArrowLeft size={16} />,
  },
}

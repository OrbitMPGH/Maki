import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { archivedLibrarySeries, libraryHandlers } from '../../../.storybook/mocks/library'
import { ArchivedList } from './ArchivedList'

const preview = http.get(api('/series/:id/archive-preview'), ({ params }) =>
  HttpResponse.json({
    freeableBytes: Number(params.id) === 12 ? 0 : 3_400_000_000,
    otherActiveReaders: 0,
    redownloadableChapters: 0,
  }),
)

const meta = {
  component: ArchivedList,
  tags: ['ai-generated'],
  args: {
    series: archivedLibrarySeries,
    selectMode: false,
    selectedIds: new Set<number>(),
    readTracking: true,
    onToggle: fn(),
    canRestore: true,
    canRemoveFiles: true,
    canDelete: true,
    onRate: fn(),
    onAction: fn(),
  },
  parameters: { layout: 'padded', msw: [preview, ...libraryHandlers] },
} satisfies Meta<typeof ArchivedList>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByRole('button', { name: /^Restore / })).toHaveLength(archivedLibrarySeries.length))
    await expect(await canvas.findByText('Removed')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByText(/GB on disk/).length).toBeGreaterThan(0))
  },
}

export const RestoreFires: Story = {
  play: async ({ args, canvas, userEvent }) => {
    const buttons = await canvas.findAllByRole('button', { name: /^Restore / })
    await userEvent.click(buttons[0])
    await expect(args.onAction).toHaveBeenCalledWith('restore', archivedLibrarySeries[0])
  },
}

export const ReadOnly: Story = {
  args: { canRestore: false, canRemoveFiles: false, canDelete: false },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(archivedLibrarySeries[0].displayTitle)).toBeVisible()
    await expect(canvas.queryByRole('button', { name: /^Restore / })).toBeNull()
  },
}

export const SelectMode: Story = {
  args: { selectMode: true, selectedIds: new Set([archivedLibrarySeries[0].id]) },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByRole('checkbox')).toHaveLength(archivedLibrarySeries.length))
    await expect(canvas.queryByRole('button', { name: /^Restore / })).toBeNull()
  },
}

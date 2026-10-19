import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import type { MetadataChange } from '../../api/seriesMetadata'
import { api, daysAgo, failWith, hoursAgo, neverResolve } from '../../../.storybook/mocks/api'
import { MetadataHistoryModal } from './MetadataHistoryModal'

const history: MetadataChange[] = [
  { id: 6, field: 'status', oldValue: 'ongoing', newValue: 'hiatus', source: 'refresh', userName: null, changedAt: hoursAgo(5) },
  { id: 5, field: 'totalChapters', oldValue: '140', newValue: '142', source: 'refresh', userName: null, changedAt: daysAgo(3) },
  { id: 4, field: 'title', oldValue: 'Lanterns Over Kasumi', newValue: 'Lanterns over Kasumi Valley', source: 'user', userName: 'maki', changedAt: daysAgo(9) },
  { id: 3, field: 'genres', oldValue: 'Fantasy, Adventure', newValue: 'Fantasy, Adventure, Drama', source: 'user', userName: null, changedAt: daysAgo(20) },
  { id: 2, field: 'overview', oldValue: null, newValue: null, source: 'user', userName: 'maki', changedAt: daysAgo(30) },
  { id: 1, field: 'cover', oldValue: null, newValue: null, source: 'user', userName: 'maki', changedAt: daysAgo(44) },
]

const meta = {
  component: MetadataHistoryModal,
  tags: ['ai-generated'],
  args: { seriesId: 1, opened: true, onClose: fn() },
  parameters: { msw: [http.get(api('/series/:id/metadata/history'), () => HttpResponse.json(history))] },
} satisfies Meta<typeof MetadataHistoryModal>

export default meta
type Story = StoryObj<typeof meta>

export const WithChanges: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Metadata history' })
    await expect((await within(dialog).findAllByText(/Metadata refresh/)).length).toBeGreaterThanOrEqual(2)
    await expect(dialog).toHaveTextContent('Set by maki')
  },
}

export const Empty: Story = {
  parameters: { msw: [http.get(api('/series/:id/metadata/history'), () => HttpResponse.json([]))] },
  play: async ({ canvasElement }) => {
    await expect(await within(canvasElement.ownerDocument.body).findByText('No changes recorded yet.')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/series/:id/metadata/history'), () => neverResolve())] },
}

export const Failed: Story = {
  parameters: { msw: [http.get(api('/series/:id/metadata/history'), () => failWith(500))] },
  // The failed request raises the app's global error toast; the modal itself just has nothing to list.
  play: async ({ canvasElement }) => {
    await expect(await within(canvasElement.ownerDocument.body).findByText('No changes recorded yet.')).toBeVisible()
  },
}

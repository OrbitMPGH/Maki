import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../../.storybook/mocks/api'
import { seriesDto, seriesMsw } from '../../../.storybook/mocks/series'
import { EditMetadataModal, MetadataLock } from './EditMetadataModal'

const meta = {
  component: EditMetadataModal,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn(), series: seriesDto(1) },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof EditMetadataModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogOf = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body).findByRole('dialog')

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(within(dialog).getByRole('textbox', { name: /title/i })).toHaveValue('Lanterns Over Kasumi')
    await expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled()
  },
}

export const EditingTitleEnablesSave: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    const title = within(dialog).getByRole('textbox', { name: /title/i })
    await userEvent.clear(title)
    await userEvent.type(title, 'Lanterns over Kasumi Valley')
    await expect(within(dialog).getByRole('button', { name: 'Save' })).toBeEnabled()
  },
}

export const SaveWritesAndCloses: Story = {
  args: { onClose: fn() },
  parameters: {
    msw: seriesMsw(
      http.put(api('/series/:id/metadata'), () => HttpResponse.json({ lockedFields: ['title'], refreshed: true, coverRestored: false })),
    ),
  },
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    const title = within(dialog).getByRole('textbox', { name: /title/i })
    await userEvent.type(title, ' II')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const SaveFails: Story = {
  parameters: { msw: seriesMsw(http.put(api('/series/:id/metadata'), () => failWith(400, 'Total chapters cannot be negative'))) },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.type(within(dialog).getByRole('textbox', { name: /title/i }), '!')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }))
  },
}

export const LockedFields: Story = {
  args: { series: seriesDto(1, { lockedFields: ['title', 'overview', 'genres', 'cover'] }) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(within(dialog).getAllByRole('button', { name: /reset to provider/i }).length).toBeGreaterThanOrEqual(2)
  },
}

export const ResetToProvider: Story = {
  args: { series: seriesDto(1, { lockedFields: ['title'] }) },
  parameters: {
    msw: seriesMsw(
      http.post(api('/series/:id/metadata/reset'), () => HttpResponse.json({ lockedFields: [], refreshed: true, coverRestored: false })),
    ),
  },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(within(dialog).getAllByRole('button', { name: /reset to provider/i })[0])
  },
}

export const Closed: Story = {
  args: { opened: false },
}

export const LockIcon: StoryObj<typeof MetadataLock> = {
  render: () => <MetadataLock size={20} />,
}

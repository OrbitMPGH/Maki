import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../.storybook/mocks/api'
import { chaptersFor, filesFor, seriesMsw } from '../../.storybook/mocks/series'
import { LinkFileToChaptersModal } from './LinkFileToChaptersModal'

const files = filesFor(1, chaptersFor(1, { missing: [17, 18] }))
const unlinkedVolume = files.find((f) => f.status === 'unlinked')!
const linkedFile = files.find((f) => f.status === 'linked')!

const meta = {
  component: LinkFileToChaptersModal,
  tags: ['ai-generated'],
  args: { seriesId: 1, file: unlinkedVolume, onClose: fn() },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof LinkFileToChaptersModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogOf = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Link chapters to this file' })

export const UnlinkedVolume: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect((await within(dialog).findAllByRole('button', { pressed: false })).length).toBeGreaterThan(5)
  },
}

export const PickChapters: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    const rows = await within(dialog).findAllByRole('button', { pressed: false })
    await userEvent.click(rows[0])
    await expect(rows[0]).toHaveAttribute('aria-pressed', 'true')
  },
}

export const SelectMissing: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click(await within(dialog).findByRole('button', { name: /select missing/i }))
  },
}

export const AlreadyLinkedFile: Story = {
  args: { file: linkedFile },
}

export const Closed: Story = {
  args: { file: null },
}

export const NoChapters: Story = {
  parameters: { msw: seriesMsw(http.get(api('/chapter'), () => HttpResponse.json([]))) },
}

export const LoadingChapters: Story = {
  parameters: { msw: seriesMsw(http.get(api('/chapter'), () => neverResolve())) },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import { compareSnapshot, seriesMsw } from '../../.storybook/mocks/series'
import type { CompareSnapshot } from '../api/types'
import { SourceCompareModal } from './SourceCompareModal'

const snapshotMsw = (snapshot: CompareSnapshot) =>
  seriesMsw(
    http.get(api('/sourcemapping/compare'), () => HttpResponse.json(snapshot)),
    http.post(api('/sourcemapping/compare'), () => HttpResponse.json(snapshot)),
  )

const meta = {
  component: SourceCompareModal,
  tags: ['ai-generated'],
  args: { seriesId: 1, opened: true, onClose: fn() },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof SourceCompareModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogOf = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Compare sources' })

export const ThreeSources: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText('Source A')).toBeVisible()
    await expect(await within(dialog).findByText('Source B')).toBeVisible()
  },
}

export const RevealSourceNames: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText('Source A')).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('switch', { name: /Hide source names/ }))
    await expect(await within(dialog).findByText('MangaDex')).toBeVisible()
  },
}

export const StillFetching: Story = {
  parameters: {
    msw: snapshotMsw(
      compareSnapshot(1, {
        running: true,
        panels: compareSnapshot(1).panels.map((p, i) => ({ ...p, status: i === 0 ? 'ready' : i === 1 ? 'fetching' : 'listing', pages: i === 0 ? p.pages : [], quality: null })),
      }),
    ),
  },
}

export const SourcesShareNoChapter: Story = {
  parameters: { msw: snapshotMsw(compareSnapshot(1, { mixedChapters: true, commonChapters: [], pagesAligned: false })) },
}

export const UnalignedEdition: Story = {
  parameters: {
    msw: snapshotMsw(
      compareSnapshot(1, { panels: compareSnapshot(1).panels.map((p, i) => (i === 1 ? { ...p, aligned: false } : p)) }),
    ),
  },
}

export const SingleSource: Story = {
  parameters: {
    msw: snapshotMsw(compareSnapshot(1, { pagesAligned: false, panels: compareSnapshot(1).panels.slice(0, 1) })),
  },
}

export const PickBetterCopy: Story = {
  args: {
    mode: 'pick',
    chapter: { id: 1120, number: 12, label: 'Ch.12', currentSourceName: 'mangadex' },
  },
  play: async ({ canvasElement }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: /find a better copy of ch\.12/i })
    await waitFor(() => expect((within(dialog).getAllByRole('button', { name: 'Use this copy' })).length).toBeGreaterThan(0))
  },
}

export const ReaderCompares: Story = {
  parameters: { auth: 'reader', msw: seriesMsw() },
  play: async ({ canvasElement }) => {
    await dialogOf(canvasElement)
  },
}

export const CompareFails: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/sourcemapping/compare'), () => failWith(500)),
      http.post(api('/sourcemapping/compare'), () => failWith(500, 'The comparison could not start')),
    ),
  },
}

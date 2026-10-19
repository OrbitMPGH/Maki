import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { releases, seriesMsw } from '../../.storybook/mocks/series'
import { ReleaseSearchModal } from './ReleaseSearchModal'

const meta = {
  component: ReleaseSearchModal,
  tags: ['ai-generated'],
  args: { seriesId: 1, opened: true, onClose: fn() },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof ReleaseSearchModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogOf = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Search releases' })

export const Results: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText(/Northwind\] Lanterns Over Kasumi v01-v05/)).toBeVisible()
    await expect(within(dialog).getAllByRole('button', { name: 'Grab' }).length).toBe(releases.length)
  },
}

export const GrabOne: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await userEvent.click((await within(dialog).findAllByRole('button', { name: 'Grab' }))[0])
  },
}

export const EditedQueryRunsAgain: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    const box = await within(dialog).findByRole('textbox', { name: 'Search query' })
    await waitFor(() => expect(box).toHaveValue('Lanterns Over Kasumi'))
    await userEvent.clear(box)
    await userEvent.type(box, 'Kasumi digital{Enter}')
    await expect(await within(dialog).findByDisplayValue('Kasumi digital')).toBeVisible()
  },
}

export const NoReleases: Story = {
  parameters: {
    msw: seriesMsw(http.get(api('/release'), () => HttpResponse.json({ query: 'Lanterns Over Kasumi', releases: [] }))),
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText(/No releases found/)).toBeVisible()
  },
}

export const WithoutUpgradeProfile: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/release'), () =>
        HttpResponse.json({ query: 'Lanterns Over Kasumi', releases: releases.map((r) => ({ ...r, parsed: null })) }),
      ),
    ),
  },
}

export const Searching: Story = {
  parameters: { msw: seriesMsw(http.get(api('/release'), () => neverResolve())) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText(/Searching indexers/)).toBeVisible()
  },
}

export const IndexerFails: Story = {
  parameters: { msw: seriesMsw(http.get(api('/release'), () => failWith(502, 'No indexer answered'))) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText(/No indexer answered/)).toBeVisible()
  },
}

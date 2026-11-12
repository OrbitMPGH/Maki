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
    await waitFor(() => expect(within(dialog).getAllByRole('button', { name: 'Grab' }).length).toBe(releases.length))
    await expect(await within(dialog).findByText(/results? from \d+ indexers?/)).toBeVisible()
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

export const NoReleasesAfterFallbacks: Story = {
  args: { titles: ['Lanterns Over Kasumi', 'Kasumi no Tomoshibi', '霞の灯'] },
  parameters: {
    msw: seriesMsw(
      http.get(api('/release'), () =>
        HttpResponse.json({
          query: 'Lanterns Over Kasumi',
          tried: ['Lanterns Over Kasumi', 'Kasumi no Tomoshibi', '霞の灯'],
          releases: [],
        }),
      ),
    ),
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText(/Searched for Lanterns Over Kasumi, Kasumi no Tomoshibi/)).toBeVisible()
    await expect(within(dialog).getByRole('textbox', { name: 'Search query' })).toHaveValue('Lanterns Over Kasumi')
  },
}

export const TitleButtonFillsTheBox: Story = {
  args: { titles: ['Lanterns Over Kasumi', 'Kasumi no Tomoshibi'] },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    const box = await within(dialog).findByRole('textbox', { name: 'Search query' })
    await waitFor(() => expect(box).toHaveValue('Lanterns Over Kasumi'))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Kasumi no Tomoshibi' }))
    await expect(box).toHaveValue('Kasumi no Tomoshibi')
    await expect(box).toHaveFocus()
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

export const NoIndexer: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/release'), () =>
        failWith(400, 'No indexer is configured (Settings → Downloads → Indexer)', 'error.release.indexerNotConfigured'),
      ),
    ),
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText('No indexer set up')).toBeVisible()
    await expect(await within(dialog).findByRole('link', { name: 'Set up an indexer' })).toHaveAttribute(
      'href',
      '/settings?tab=downloads&s=indexer',
    )
    await expect(within(dialog).queryByRole('textbox', { name: 'Search query' })).toBeNull()
  },
}

export const IndexerFails: Story = {
  parameters: { msw: seriesMsw(http.get(api('/release'), () => failWith(502, 'No indexer answered'))) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText(/No indexer answered/)).toBeVisible()
  },
}

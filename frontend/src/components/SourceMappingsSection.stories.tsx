import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, hoursAgo } from '../../.storybook/mocks/api'
import { mappingsFor, seriesMsw, sourceOrderFor } from '../../.storybook/mocks/series'
import type { ScoutSnapshot, SourceOrderDto } from '../api/upgrades'
import { SourceMappingsSection } from './SourceMappingsSection'

const withOrder = (patch: Partial<SourceOrderDto>) =>
  seriesMsw(http.get(api('/sourcemapping/quality'), () => HttpResponse.json({ ...sourceOrderFor(1), ...patch })))

const finishedScout: ScoutSnapshot = {
  running: false,
  probes: 6,
  done: 6,
  measured: 6,
  startedAtUtc: hoursAgo(2),
  finishedAtUtc: hoursAgo(1),
  chapters: ['Ch. 12', 'Ch. 60', 'Ch. 131'],
  sources: [
    { mappingId: 11, state: 'done', planned: 3, done: 3, measured: 3, problem: null },
    { mappingId: 12, state: 'done', planned: 3, done: 3, measured: 3, problem: null },
  ],
}

const meta = {
  component: SourceMappingsSection,
  tags: ['ai-generated'],
  args: { seriesId: 1, seriesTitle: 'Lanterns Over Kasumi' },
  parameters: { layout: 'padded', msw: seriesMsw() },
} satisfies Meta<typeof SourceMappingsSection>

export default meta
type Story = StoryObj<typeof meta>

export const Linked: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('MangaDex')).toBeVisible()
    await expect(await canvas.findByText('Comick')).toBeVisible()
  },
}

export const NoSourcesLinked: Story = {
  parameters: { msw: seriesMsw(http.get(api('/sourcemapping'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/No sources linked/)).toBeVisible()
  },
}

export const MatchingInBackground: Story = {
  args: { matching: true },
  parameters: { msw: seriesMsw(http.get(api('/sourcemapping'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Searching sources for a match|Matching the remaining sources/)).toBeVisible()
  },
}

export const BestQualityOrder: Story = {
  parameters: { msw: withOrder({ seriesMode: 'quality', mode: 'quality' }) },
}

export const MeasurementRunning: Story = {
  parameters: {
    msw: withOrder({
      scout: {
        ...finishedScout,
        running: true,
        done: 2,
        measured: 2,
        finishedAtUtc: null,
        sources: [
          { mappingId: 11, state: 'measuring', planned: 3, done: 2, measured: 2, problem: null },
          { mappingId: 12, state: 'waiting', planned: 3, done: 0, measured: 0, problem: null },
        ],
      },
    }),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect((canvas.getAllByText(/Measuring/)).length).toBeGreaterThan(0))
  },
}

export const MeasurementFinishedSuggestsReorder: Story = {
  parameters: {
    msw: withOrder({ scout: finishedScout, order: [12, 11, 13], qualityOrder: [11, 12] }),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Use best quality first' })).toBeVisible()
  },
}

export const MeasurementFailed: Story = {
  parameters: {
    msw: withOrder({
      scout: {
        ...finishedScout,
        measured: 0,
        sources: [
          { mappingId: 11, state: 'failed', planned: 3, done: 3, measured: 0, problem: 'failed' },
          { mappingId: 12, state: 'failed', planned: 3, done: 3, measured: 0, problem: 'cooldown' },
        ],
      },
    }),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Nothing could be measured/)).toBeVisible()
  },
}

export const SourceSwitchedOffGlobally: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/sourcemapping'), () =>
        HttpResponse.json([{ ...mappingsFor(1)[0], sourceName: 'shonenplaza' }, ...mappingsFor(1).slice(1)]),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Source off')).toBeVisible()
  },
}

export const RemoveAsksForCleanup: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const buttons = await canvas.findAllByRole('button', { name: 'Remove mapping' })
    await userEvent.click(buttons[0])
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(dialog).toHaveTextContent(/Remove/))
    await expect(await within(dialog).findByRole('button', { name: 'Remove and clean up' })).toBeVisible()
  },
}

export const LinkSourceSearch: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const open = await canvas.findByRole('button', { name: /link source/i })
    await waitFor(() => expect(open).toBeEnabled())
    await userEvent.click(open)
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Link a source' })
    // Opens seeded with the series title, so the first search runs without typing.
    await expect(await within(dialog).findByText('Lanterns Over Kasumi: Side Stories')).toBeVisible()
    await waitFor(() => expect(within(dialog).getAllByRole('link', { name: 'Open source page' })).toHaveLength(2))
  },
}

export const MappingsFailToLoad: Story = {
  parameters: { msw: seriesMsw(http.get(api('/sourcemapping'), () => failWith(500))) },
}

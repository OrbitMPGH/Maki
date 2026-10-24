import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import {
  activityHandler,
  insightsHandler,
  libraryGrowthHandler,
  libraryHandler,
  progressSummaryNewcomer,
  progressSummaryOff,
  statsPageBase,
  standingHandler,
  yearsHandler,
} from '../../.storybook/mocks/insights'
import StatsPage from './StatsPage'

const meta = {
  component: StatsPage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', router: { initialEntries: ['/stats'] }, msw: statsPageBase() },
} satisfies Meta<typeof StatsPage>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Stats' })).toBeVisible()
    await expect(await canvas.findByText('Day by day')).toBeVisible()
    await expect(await canvas.findByText('Hour of day and weekday')).toBeVisible()
    await expect(await canvas.findByText('Finish rate')).toBeVisible()
    await expect(await canvas.findByText('Growth')).toBeVisible()
    await expect(await canvas.findByText('Level 12')).toBeVisible()
  },
}

/** An admin with other accounts on the instance can look at a reader's numbers; Rewind is only for your own. */
export const AdminReaderPicker: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const picker = await canvas.findByRole('combobox', { name: 'Reader' })
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Play Rewind' })).toBeEnabled())
    await userEvent.click(picker)
    await userEvent.click(await within(canvasElement.ownerDocument.body).findByRole('option', { name: 'Rin' }))
    await expect(canvas.queryByRole('button', { name: 'Play Rewind' })).toBeNull()
  },
}

export const Reader: Story = {
  parameters: { auth: 'reader', msw: statsPageBase() },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Day by day')).toBeVisible()
    await expect(canvas.queryByRole('combobox', { name: 'Reader' })).toBeNull()
  },
}

export const AllTime: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('radio', { name: 'All time' }))
    await waitFor(() => expect(canvas.getByRole('radio', { name: 'All time' })).toBeChecked())
    await expect(await canvas.findByText('Day by day')).toBeVisible()
  },
}

export const YearDrillDown: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('radio', { name: 'Year' }))
    await waitFor(() => expect(canvas.getByRole('combobox', { name: 'Year' })).toHaveValue('2026'))
    await waitFor(() => expect(canvas.getByRole('combobox', { name: 'Month' })).toHaveValue('Whole year'))
  },
}

export const DeepLinkedSection: Story = {
  parameters: { router: { initialEntries: ['/stats?section=progress'] }, msw: statsPageBase() },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('region', { name: 'Progress' })).toBeVisible()
  },
}

export const RewindOpens: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const play = await canvas.findByRole('button', { name: 'Play Rewind' })
    // Disabled until the year's activity has loaded.
    await waitFor(() => expect(play).toBeEnabled())
    await userEvent.click(play)
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('dialog', { name: '2026 Rewind' })).toBeVisible()
    await userEvent.click(await body.findByRole('button', { name: 'Close Rewind' }))
  },
}

/** A reader who only has Kavita-reported reads, or nothing yet, gets guidance in place of charts. */
export const ReadTrackingOff: Story = {
  parameters: {
    msw: [
      ...statsPageBase(),
      activityHandler('untracked'),
      http.get(api('/stats/years'), () => HttpResponse.json([])),
    ],
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect((canvas.getAllByText('No reading tracked yet')).length).toBeGreaterThan(0))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Play Rewind' })).toBeDisabled())
  },
}

/** A first week: a handful of chapters, no history to chart, nothing to compare. */
export const NewReader: Story = {
  parameters: {
    msw: [
      ...statsPageBase(progressSummaryNewcomer),
      activityHandler('sparse'),
      insightsHandler('sparse'),
      standingHandler('sparse'),
      libraryHandler('empty'),
      libraryGrowthHandler('empty'),
      yearsHandler([2026]),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Day by day')).toBeVisible()
    await expect(await canvas.findByText('No timed reading yet')).toBeVisible()
  },
}

export const ProgressSwitchedOff: Story = {
  parameters: { msw: statsPageBase(progressSummaryOff) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Progress tracking is switched off/)).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: {
    msw: [
      ...statsPageBase(),
      http.get(api('/stats/activity'), () => neverResolve()),
      http.get(api('/stats/insights'), () => neverResolve()),
      http.get(api('/stats/standing'), () => neverResolve()),
      http.get(api('/stats/library'), () => neverResolve()),
      http.get(api('/stats/years'), () => neverResolve()),
    ],
  },
}

export const ServerError: Story = {
  parameters: {
    msw: [
      ...statsPageBase(),
      http.get(api('/stats/activity'), () => failWith(500)),
      http.get(api('/stats/insights'), () => failWith(500)),
      http.get(api('/stats/standing'), () => failWith(500)),
      http.get(api('/stats/library'), () => failWith(500)),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Could not load library stats')).toBeVisible()
  },
}

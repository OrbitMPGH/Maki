import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../../../.storybook/mocks/api'
import {
  activityHandler,
  insightsFor,
  insightsHandler,
  sectionProps,
  standingFor,
  standingHandler,
} from '../../../../.storybook/mocks/insights'
import TasteSection from './TasteSection'

const meta = {
  component: TasteSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <div className="stats-layout">
        <Story />
      </div>
    ),
  ],
  args: sectionProps,
} satisfies Meta<typeof TasteSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Lean')).toBeVisible()
    await expect(await canvas.findByText('You vs the crowd')).toBeVisible()
    await expect(await canvas.findByText('Creators you return to')).toBeVisible()
    await expect(await canvas.findByText('Found Family')).toBeVisible()
    await expect(await canvas.findByText(/You rate 0.6/)).toBeVisible()
  },
}

const newReaderStanding = () => {
  const base = standingFor('sparse')
  return { ...base, behaviour: { ...base.behaviour, chaptersRead: 0, readingDays: 0 } }
}

/** A new reader has no history at all, so there is nothing to wait for but more reading. */
export const NewReader: Story = {
  parameters: {
    msw: [
      activityHandler('sparse'),
      insightsHandler('sparse'),
      http.get(api('/stats/standing'), () => HttpResponse.json(newReaderStanding())),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Not enough reading yet to say what your taste is/)).toBeVisible()
    await expect(canvas.queryByText('Lean')).toBeNull()
  },
}

/** A long-standing reader looking at a window with nothing in it is not a new reader. */
export const QuietWindow: Story = {
  parameters: { msw: [activityHandler('sparse'), insightsHandler('sparse'), standingHandler('sparse')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Nothing read in this window/)).toBeVisible()
    await expect(canvas.queryByText(/Not enough reading yet/)).toBeNull()
  },
}

/** A month of one kind of manga: type, demographic and era each have a single value. */
export const SingleValues: Story = {
  parameters: {
    msw: [
      activityHandler(),
      http.get(api('/stats/insights'), ({ request }) => {
        const params = new URL(request.url).searchParams
        const base = insightsFor(params.get('from') ?? '2026-09-20', params.get('to') ?? '2026-10-19', 'rich')
        return HttpResponse.json({
          ...base,
          taste: {
            ...base.taste,
            types: [{ name: 'Manga', count: 84 }],
            demographics: [{ name: 'Shounen', count: 84 }],
            eras: [{ decade: 2020, chapters: 84 }],
          },
        })
      }),
      standingHandler(),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Everything you read was/)).toBeVisible()
    await expect(canvas.queryByText('Type mix')).toBeNull()
    await expect(canvas.queryByText('Era')).toBeNull()
  },
}

/** An untagged library: the one bucket of each is Unknown or Unspecified, so the panels stay. */
export const UnknownBuckets: Story = {
  parameters: {
    msw: [
      activityHandler(),
      http.get(api('/stats/insights'), ({ request }) => {
        const params = new URL(request.url).searchParams
        const base = insightsFor(params.get('from') ?? '2026-09-20', params.get('to') ?? '2026-10-19', 'rich')
        return HttpResponse.json({
          ...base,
          taste: {
            ...base.taste,
            types: [{ name: 'unknown', count: 84 }],
            demographics: [{ name: '', count: 84 }],
            eras: [{ decade: null, chapters: 84 }],
          },
        })
      }),
      standingHandler(),
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Type mix')).toBeVisible()
    await expect(await canvas.findByText('Demographic')).toBeVisible()
    await expect(await canvas.findByText('Era')).toBeVisible()
    await expect(canvas.queryByText(/Everything you read/)).toBeNull()
  },
}

export const ReadTrackingOff: Story = {
  parameters: { msw: [activityHandler('untracked')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No reading tracked yet')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: {
    msw: [
      http.get(api('/stats/activity'), () => neverResolve()),
      http.get(api('/stats/insights'), () => neverResolve()),
      http.get(api('/stats/standing'), () => neverResolve()),
    ],
  },
}

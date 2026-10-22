import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../../../.storybook/mocks/api'
import {
  activityHandler,
  insightsFor,
  insightsHandler,
  sectionProps,
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

/** A new reader has no genre history, no ratings and no repeat creators yet. */
export const NewReader: Story = {
  parameters: { msw: [activityHandler('sparse'), insightsHandler('sparse'), standingHandler('sparse')] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Not enough reading yet to say what your taste is/)).toBeVisible()
    await expect(canvas.queryByText('Lean')).toBeNull()
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

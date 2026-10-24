import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import { seriesMsw } from '../../.storybook/mocks/series'
import { uiSettings } from '../../.storybook/mocks/shared'
import { RelatedSeriesSection } from './RelatedSeriesSection'

const meta = {
  component: RelatedSeriesSection,
  tags: ['ai-generated'],
  args: { seriesId: 1 },
  parameters: { layout: 'padded', msw: seriesMsw() },
} satisfies Meta<typeof RelatedSeriesSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Related series' })).toBeVisible()
  },
}

export const NothingRelated: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/related'), () => HttpResponse.json([]))) },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('h2, h3')).toBeNull()
  },
}

export const HiddenInSettings: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/settings/ui'), () => HttpResponse.json({ ...uiSettings, seriesSections: { related: false, similar: true } })),
    ),
  },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('heading', { name: 'Related series' })).toBeNull()
  },
}

export const RequestFails: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/related'), () => failWith(500))) },
}

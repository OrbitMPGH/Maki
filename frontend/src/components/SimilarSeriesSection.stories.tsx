import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import { seriesMsw } from '../../.storybook/mocks/series'
import { uiSettings } from '../../.storybook/mocks/shared'
import { SimilarSeriesSection } from './SimilarSeriesSection'

const meta = {
  component: SimilarSeriesSection,
  tags: ['ai-generated'],
  args: { seriesId: 1 },
  parameters: { layout: 'padded', msw: seriesMsw() },
} satisfies Meta<typeof SimilarSeriesSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'More like this' })).toBeVisible()
  },
}

export const NothingSimilar: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/similar'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('heading', { name: 'More like this' })).toBeNull()
  },
}

export const HiddenInSettings: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/settings/ui'), () => HttpResponse.json({ ...uiSettings, seriesSections: { related: true, similar: false } })),
    ),
  },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('heading', { name: 'More like this' })).toBeNull()
  },
}

export const RequestFails: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/similar'), () => failWith(500))) },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import {
  discoverPage,
  readingBehaviourEmpty,
  resetTabState,
  tasteInsightsUnavailable,
  tasteProfileEmpty,
} from '../../../.storybook/mocks/discover'
import { TasteTab } from './TasteTab'

const meta = {
  component: TasteTab,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof TasteTab>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('How you read', {}, { timeout: 8000 })).toBeVisible()
    await expect(await canvas.findByRole('heading', { level: 3, name: 'Found Family + Travel' })).toBeVisible()
    await expect(await canvas.findByText('The odd one out')).toBeVisible()
    await expect(await canvas.findByText('Outside your core')).toBeVisible()
    await expect(await canvas.findByText('From your own shelf')).toBeVisible()
    await expect(await canvas.findByText("More than you'd expect")).toBeVisible()
    await expect(await canvas.findByText('How deep you go')).toBeVisible()
    await expect(await canvas.findByText('You collect these, but rarely open them')).toBeVisible()
    await expect(await canvas.findByText('The length you finish')).toBeVisible()
    await expect(await canvas.findByText(/read in one or two days/)).toBeVisible()
    await expect(await canvas.findByText('What steers your picks most')).toBeVisible()
    await expect(await canvas.findByText('Recommendations you took up')).toBeVisible()
  },
}

export const ShelfView: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByText('Everything you own'))
    await expect(await canvas.findByText(/across all 10 series you own/)).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/recommendations/taste-insights'), () => neverResolve()),
      http.get(api('/recommendations/reading-behaviour'), () => neverResolve()),
      http.get(api('/recommendations/taste-profile'), () => neverResolve()),
    ),
  },
}

export const ProfileError: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/taste-profile'), () => failWith(500, 'The profile service is down'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Could not read your profile')).toBeVisible()
  },
}

export const NothingYet: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/recommendations/taste-profile'), () => HttpResponse.json(tasteProfileEmpty)),
      http.get(api('/recommendations/taste-insights'), () => HttpResponse.json(tasteInsightsUnavailable)),
      http.get(api('/recommendations/reading-behaviour'), () => HttpResponse.json(readingBehaviourEmpty)),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing to profile yet')).toBeVisible()
  },
}

export const IndexNotBuilt: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/taste-insights'), () => HttpResponse.json(tasteInsightsUnavailable))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/embedding index is not built yet/)).toBeVisible()
  },
}

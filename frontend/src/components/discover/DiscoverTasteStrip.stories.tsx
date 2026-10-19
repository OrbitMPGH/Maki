import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../../.storybook/mocks/api'
import { discoverPage, tasteProfileEmpty } from '../../../.storybook/mocks/discover'
import { DiscoverTasteStrip } from './DiscoverTasteStrip'

const meta = {
  component: DiscoverTasteStrip,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverTasteStrip>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Steering these picks')).toBeVisible()
    await expect(canvas.getByRole('link', { name: 'Your taste' })).toHaveAttribute('href', '/discover/taste')
  },
}

export const Loading: Story = {
  parameters: { msw: discoverPage(http.get(api('/recommendations/taste-profile'), () => neverResolve())) },
}

export const NoTagsYet: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/taste-profile'), () => HttpResponse.json(tasteProfileEmpty))),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.queryByText('Steering these picks')).toBeNull())
  },
}

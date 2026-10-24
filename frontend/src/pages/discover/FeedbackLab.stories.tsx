import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { discoverPage, feedbackLab, feedbackLabEmpty, resetTabState } from '../../../.storybook/mocks/discover'
import { SignalsCard } from './FeedbackLab'

const meta = {
  component: SignalsCard,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof SignalsCard>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('titles on the shelf')).toBeVisible()
    await expect(await canvas.findByText('Recent feedback')).toBeVisible()
  },
}

export const NoFeedbackYet: Story = {
  parameters: { msw: discoverPage(http.get(api('/recommendations/feedback-lab'), () => HttpResponse.json(feedbackLabEmpty))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Thumbs, hide or dismiss a recommendation/)).toBeVisible()
  },
}

export const FallbackRanking: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/recommendations/feedback-lab'), () => HttpResponse.json({ ...feedbackLab, rankingMode: 'fallback' })),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Catalogue fallback is active/)).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: discoverPage(http.get(api('/recommendations/feedback-lab'), () => neverResolve())) },
}

export const LoadError: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/feedback-lab'), () => failWith(500, 'Feedback store unavailable'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Could not load your signals/)).toBeVisible()
  },
}

export const HowItWorks: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'How signals work' }))
    const note = await canvas.findByText('Thumbs up')
    await waitFor(() => expect(note).toBeVisible())
  },
}

export const ManageOpened: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Manage signals' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByPlaceholderText('Search titles')).toBeVisible()
  },
}

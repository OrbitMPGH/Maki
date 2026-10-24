import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../../.storybook/mocks/api'
import { discoverPage, resetTabState } from '../../../.storybook/mocks/discover'
import { RecommendationFeedbackMenu } from './RecommendationFeedbackMenu'

const meta = {
  component: RecommendationFeedbackMenu,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  // 30000 has no recorded feedback; 30001 is thumbed up, 30007 is thumbed down and hidden.
  args: { providerId: '30000' },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof RecommendationFeedbackMenu>

export default meta
type Story = StoryObj<typeof meta>

const body = (el: HTMLElement) => within(el.ownerDocument.body)

// The buttons stay in their loading state until the title's feedback state has been read.
const ready = async (canvas: ReturnType<typeof within>, name: string) => {
  const button = await canvas.findByRole('button', { name })
  await waitFor(() => expect(button).toBeEnabled())
  return button
}

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await ready(canvas, 'Thumbs up')).toHaveAttribute('aria-pressed', 'false')
  },
}

export const ThumbsUp: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await ready(canvas, 'Thumbs up'))
    await expect(await body(canvasElement).findByText(/Thumbs up\. This title now steers/)).toBeVisible()
  },
}

export const AlreadyLiked: Story = {
  args: { providerId: '30001' },
  play: async ({ canvas }) => {
    const button = await ready(canvas, 'Thumbs up')
    await waitFor(() => expect(button).toHaveAttribute('aria-pressed', 'true'))
  },
}

export const DislikedAndHidden: Story = {
  args: { providerId: '30007' },
  play: async ({ canvas, userEvent, canvasElement }) => {
    const down = await ready(canvas, 'Thumbs down')
    await waitFor(() => expect(down).toHaveAttribute('aria-pressed', 'true'))
    await userEvent.click(await canvas.findByRole('button', { name: 'More recommendation feedback actions' }))
    const restore = await body(canvasElement).findByRole('menuitem', { name: 'Restore hidden or dismissed title' })
    await waitFor(() => expect(restore).toBeEnabled())
    await waitFor(() => expect(body(canvasElement).getByRole('menuitem', { name: 'Hide this title' })).toBeDisabled())
  },
}

export const MenuOpen: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More recommendation feedback actions' }))
    await expect(await body(canvasElement).findByRole('menuitem', { name: 'Hide this title' })).toBeInTheDocument()
    await expect(await body(canvasElement).findByRole('menuitem', { name: 'Dismiss for 30 days' })).toBeInTheDocument()
    await expect(await body(canvasElement).findByRole('menuitem', { name: 'Seen anime' })).toBeInTheDocument()
  },
}

export const HideFranchise: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More recommendation feedback actions' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Hide this and its franchise' }))
    await expect(await body(canvasElement).findByText(/Hidden 3 titles/)).toBeVisible()
  },
}

export const UpdateFails: Story = {
  parameters: {
    msw: discoverPage(http.put(api('/recommendations/feedback/:id'), () => failWith(500, 'The feedback store is read-only'))),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await ready(canvas, 'Thumbs down'))
    await expect(await body(canvasElement).findByText(/Could not update feedback/)).toBeVisible()
  },
}

export const NoChange: Story = {
  parameters: {
    msw: discoverPage(
      http.put(api('/recommendations/feedback/:id'), () =>
        HttpResponse.json({
          changed: false,
          eventId: null,
          state: {
            mangaBakaId: 30000,
            suppression: 'hidden',
            exposure: [],
            dismissedUntilUtc: null,
            revision: 3,
            title: null,
            sentiment: 'none',
            coverUrl: null,
            genres: null,
            updatedAtUtc: null,
          },
          feedbackRevision: 8,
          signalRevision: 3,
          queueEffect: 'unchanged',
          tasteEffect: 'unchanged',
          feedbackEffect: 'neutral',
        }),
      ),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await ready(canvas, 'Thumbs up'))
    await expect(await body(canvasElement).findByText(/No change: this title was already in that state/)).toBeVisible()
  },
}

export const NotACatalogueId: Story = {
  args: { providerId: 'activity-row' },
}

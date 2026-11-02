import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../../.storybook/mocks/api'
import { discoverPage, feedbackLab, resetTabState } from '../../../.storybook/mocks/discover'
import { ManageSignalsModal } from './ManageSignalsModal'

const meta = {
  component: ManageSignalsModal,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  args: { opened: true, onClose: fn() },
  parameters: { layout: 'fullscreen', msw: discoverPage() },
} satisfies Meta<typeof ManageSignalsModal>

export default meta
type Story = StoryObj<typeof meta>

const body = (el: HTMLElement) => within(el.ownerDocument.body)

export const Titles: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await body(canvasElement).findByRole('dialog')
    await expect(await within(dialog).findByText(/All \(\d+\)/)).toBeVisible()
    await expect(await within(dialog).findByText('Lanterns Over Kasumi')).toBeVisible()
  },
}

export const FilteredToHidden: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await body(canvasElement).findByRole('dialog')
    await userEvent.click(await within(dialog).findByText(/^Hidden \(/))
    await expect(await within(dialog).findByText(/Showing \d+ of \d+/)).toBeVisible()
  },
}

export const SearchNoMatches: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await body(canvasElement).findByRole('dialog')
    await userEvent.type(await within(dialog).findByPlaceholderText('Search titles'), 'zzzz nothing')
    await expect(await within(dialog).findByText('No titles match.')).toBeVisible()
  },
}

export const AnimeTab: Story = {
  args: { initialTab: 'anime' },
  play: async ({ canvasElement }) => {
    const dialog = await body(canvasElement).findByRole('dialog')
    await expect(await within(dialog).findByRole('tab', { name: 'Anime' })).toBeVisible()
  },
}

export const NoAnimeSignals: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/recommendations/feedback-lab'), () =>
        HttpResponse.json({ ...feedbackLab, capabilities: { ...feedbackLab.capabilities, animeSignals: false } }),
      ),
    ),
  },
  play: async ({ canvasElement }) => {
    const dialog = await body(canvasElement).findByRole('dialog')
    await within(dialog).findByText('Lanterns Over Kasumi')
    await expect(within(dialog).queryByRole('tab', { name: 'Anime' })).toBeNull()
  },
}

export const LoadFailed: Story = {
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/feedback-lab'), () => failWith(500, 'Feedback store unavailable'))),
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import {
  importEmpty,
  importFailed,
  importReady,
  importRunning,
  ratingImportHandlers,
} from '../../.storybook/mocks/insights'
import { RatingImportModal } from './RatingImportModal'

const meta = {
  component: RatingImportModal,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: { service: 'anilist', label: 'AniList', opened: true, onClose: fn() },
} satisfies Meta<typeof RatingImportModal>

export default meta
type Story = StoryObj<typeof meta>

const bodyOf = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

export const Ready: Story = {
  play: async ({ canvasElement }) => {
    const body = bodyOf(canvasElement)
    await expect(await body.findByRole('dialog', { name: 'Import ratings from AniList' })).toBeVisible()
    await expect(await body.findByText('6 of 6 selected')).toBeVisible()
    await waitFor(() => expect(body.getByRole('button', { name: 'Apply 6' })).toBeEnabled())
  },
}

export const ApplyingASelection: Story = {
  play: async ({ canvasElement, userEvent, args }) => {
    const body = bodyOf(canvasElement)
    await body.findByText('6 of 6 selected')
    await userEvent.click(body.getByRole('checkbox', { name: 'Lanterns Over Kasumi' }))
    await userEvent.click(body.getByRole('checkbox', { name: 'Snowfield Letters' }))
    await expect(body.getByText('4 of 6 selected')).toBeVisible()
    await userEvent.click(body.getByRole('button', { name: 'Apply 4' }))
    await expect(await body.findByText('Imported 4 ratings')).toBeVisible()
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const SelectNone: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = bodyOf(canvasElement)
    await body.findByText('6 of 6 selected')
    await userEvent.click(body.getByRole('checkbox', { name: /selected/ }))
    await expect(body.getByText('0 of 6 selected')).toBeVisible()
    await expect(body.getByRole('button', { name: 'Apply' })).toBeDisabled()
  },
}

export const ReadingFromTracker: Story = {
  parameters: { msw: ratingImportHandlers(importRunning) },
  play: async ({ canvasElement }) => {
    await expect(await bodyOf(canvasElement).findByText('Reading your ratings from AniList…')).toBeVisible()
  },
}

export const NothingDiffers: Story = {
  parameters: { msw: ratingImportHandlers(importEmpty) },
  play: async ({ canvasElement }) => {
    await expect(await bodyOf(canvasElement).findByText(/Nothing to import/)).toBeVisible()
  },
}

/** The tracker answered, but with an error of its own. */
export const TrackerError: Story = {
  parameters: { msw: ratingImportHandlers(importFailed) },
  play: async ({ canvasElement }) => {
    const body = bodyOf(canvasElement)
    await expect(await body.findByText(/AniList refused the saved token/)).toBeVisible()
    await expect(body.getByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

export const RequestFailed: Story = {
  parameters: {
    msw: [
      http.post(api('/scrobble/import-ratings/:service/preview'), () => failWith(502, 'Could not reach AniList.')),
      ...ratingImportHandlers(importReady),
    ],
  },
  play: async ({ canvasElement }) => {
    await expect(await bodyOf(canvasElement).findByText('Could not reach AniList.')).toBeVisible()
  },
}

export const Closed: Story = {
  args: { opened: false },
  play: async ({ canvasElement }) => {
    await expect(bodyOf(canvasElement).queryByRole('dialog')).toBeNull()
  },
}

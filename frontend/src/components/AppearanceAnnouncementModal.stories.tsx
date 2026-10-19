import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import AppearanceAnnouncementModal from './AppearanceAnnouncementModal'

const announce = (language: boolean, appearance: boolean) =>
  http.get(api('/settings/announcements'), () => HttpResponse.json({ language, appearance }))

const meta = {
  component: AppearanceAnnouncementModal,
  tags: ['ai-generated'],
  parameters: { msw: [announce(false, true)] },
} satisfies Meta<typeof AppearanceAnnouncementModal>

export default meta
type Story = StoryObj<typeof meta>

export const Pending: Story = {
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'More ways to make Maki yours' })
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(within(dialog).getByText('New default')).toBeVisible()
    await expect(within(dialog).getByText('Previous default')).toBeVisible()
  },
}

export const PickBackground: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'More ways to make Maki yours' })
    const night = within(dialog).getByRole('radio', { name: /Night/ })
    await userEvent.click(night)
    await expect(night).toHaveAttribute('aria-checked', 'true')
  },
}

export const DoneMarksItSeen: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'More ways to make Maki yours' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(body.queryByRole('dialog')).toBeNull())
  },
}

// The language notice goes first, so an account with both pending is never shown two dialogs at once.
export const WaitsForLanguageNotice: Story = {
  parameters: { msw: [announce(true, true)] },
  play: async ({ canvasElement }) => {
    await new Promise((resolve) => setTimeout(resolve, 150))
    await expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).toBeNull()
  },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import LanguageAnnouncementModal from './LanguageAnnouncementModal'

const announce = (language: boolean, appearance = false) =>
  http.get(api('/settings/announcements'), () => HttpResponse.json({ language, appearance }))

const meta = {
  component: LanguageAnnouncementModal,
  tags: ['ai-generated'],
  parameters: { msw: [announce(true)] },
} satisfies Meta<typeof LanguageAnnouncementModal>

export default meta
type Story = StoryObj<typeof meta>

export const Pending: Story = {
  play: async ({ canvasElement }) => {
    // Mantine portals the modal to document.body.
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Maki speaks your language' })
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(within(dialog).getByText(/translated into \d+ languages/)).toBeVisible()
    await expect(within(dialog).getByLabelText('Language')).toBeVisible()
  },
}

export const DoneMarksItSeen: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Maki speaks your language' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Done' }))
    await waitFor(() => expect(body.queryByRole('dialog', { name: 'Maki speaks your language' })).toBeNull())
  },
}

export const AlreadySeen: Story = {
  parameters: { msw: [announce(false)] },
  play: async ({ canvasElement }) => {
    // Give the announcements request time to answer before checking nothing opened.
    await new Promise((resolve) => setTimeout(resolve, 150))
    await expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).toBeNull()
  },
}

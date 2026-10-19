import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { OfflineStartPage } from './OfflineStartPage'

const meta = {
  component: OfflineStartPage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', auth: 'signedOut' },
} satisfies Meta<typeof OfflineStartPage>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('heading', { name: "You're offline" })).toBeVisible()
    await expect(canvas.getByText(/Maki can't reach the server/)).toBeVisible()
  },
}

export const TryAgain: Story = {
  play: async ({ canvas, userEvent }) => {
    // Retrying re-asks the server who is signed in; the page stays up while it answers.
    await userEvent.click(canvas.getByRole('button', { name: 'Try again' }))
    await expect(canvas.getByRole('heading', { name: "You're offline" })).toBeVisible()
  },
}

export const Mobile: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
}

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { progressSummary } from '../../.storybook/mocks/shell'
import { UserMenu } from './UserMenu'

const meta = {
  component: UserMenu,
  tags: ['ai-generated'],
  parameters: { layout: 'centered' },
  decorators: [
    // The menu opens upwards from the foot of the sidebar, so give it a sidebar's width and some headroom.
    (Story) => (
      <div style={{ width: 260, paddingTop: 220 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof UserMenu>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Maki')).toBeVisible()
    await expect(await canvas.findByText('Level 12')).toBeVisible()
  },
}

export const Reader: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Rin')).toBeVisible()
  },
}

export const ProgressDisabled: Story = {
  parameters: {
    msw: [http.get(api('/progress/summary'), () => HttpResponse.json({ ...progressSummary, enabled: false }))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Maki')).toBeVisible()
    await expect(canvas.queryByText(/^Level/)).toBeNull()
  },
}

export const MenuOpen: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Maki/ }))
    // Mantine renders the menu in a portal on document.body.
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByRole('menuitem', { name: 'My account' })).toBeVisible())
    await expect(await body.findByText('Administrator')).toBeVisible()
    await expect(await body.findByRole('menuitem', { name: 'Sign out' })).toBeVisible()
  },
}

export const MenuOpenAsReader: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Rin/ }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByRole('menuitem', { name: 'My account' })).toBeVisible())
    await expect(body.queryByText('Administrator')).toBeNull()
  },
}

export const SignOut: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Maki/ }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('menuitem', { name: 'Sign out' }))
    // Clearing the cached identity is the whole effect, the app's gate does the rest.
    await waitFor(() => expect(canvas.queryByText('Maki')).toBeNull())
  },
}

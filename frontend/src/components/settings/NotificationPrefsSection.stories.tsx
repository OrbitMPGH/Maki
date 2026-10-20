import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { NotificationPrefsSection } from './NotificationPrefsSection'

const meta = {
  component: NotificationPrefsSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 760 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof NotificationPrefsSection>

export default meta
type Story = StoryObj<typeof meta>

export const Admin: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('switch', { name: 'Health issue' })).toBeInTheDocument()
    await waitFor(() => expect(canvas.getByRole('switch', { name: 'New chapters available' })).toBeChecked())
    await waitFor(() => expect(canvas.getByRole('switch', { name: 'Chapters downloaded automatically' })).not.toBeChecked())
  },
}

export const Reader: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('switch', { name: 'New chapters available' })).toBeInTheDocument()
    await expect(canvas.queryByRole('switch', { name: 'Health issue' })).toBeNull()
    await expect(canvas.queryByRole('switch', { name: 'Update available' })).toBeNull()
  },
}

export const ToggleASwitch: Story = {
  play: async ({ canvas, userEvent }) => {
    const toggle = await canvas.findByRole('switch', { name: 'Level up' })
    await waitFor(() => expect(toggle).toBeChecked())
    await userEvent.click(toggle)
    await waitFor(() => expect(toggle).not.toBeChecked())
  },
}

export const SeriesDefaultReading: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/inbox/prefs'), () =>
        HttpResponse.json({ types: {}, toasts: false, seriesDefault: 'Reading' }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: /^Show a popup when a notification arrives/ })).not.toBeChecked())
  },
}

export const Loading: Story = {
  parameters: { msw: settingsMsw(http.get(api('/inbox/prefs'), () => neverResolve())) },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('heading', { name: 'Notifications' })).toBeNull()
  },
}

export const LoadFailed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/inbox/prefs'), () => failWith(500))) },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('heading', { name: 'Notifications' })).toBeNull()
  },
}

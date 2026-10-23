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
    await expect(await canvas.findByRole('checkbox', { name: 'Health issue in the bell' })).toBeInTheDocument()
    await waitFor(() => expect(canvas.getByRole('checkbox', { name: 'New chapters available in the bell' })).toBeChecked())
    await waitFor(() =>
      expect(canvas.getByRole('checkbox', { name: 'Chapters downloaded automatically in the bell' })).not.toBeChecked(),
    )
    await waitFor(() =>
      expect(canvas.getByRole('checkbox', { name: 'New chapters available as a push notification' })).toBeChecked(),
    )
  },
}

export const Reader: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('checkbox', { name: 'New chapters available in the bell' })).toBeInTheDocument()
    await expect(canvas.queryByRole('checkbox', { name: 'Health issue in the bell' })).toBeNull()
    await expect(canvas.queryByRole('checkbox', { name: 'Update available as a push notification' })).toBeNull()
  },
}

export const ToggleABellEvent: Story = {
  play: async ({ canvas, userEvent }) => {
    const toggle = await canvas.findByRole('checkbox', { name: 'Level up in the bell' })
    await waitFor(() => expect(toggle).toBeChecked())
    await userEvent.click(toggle)
    await waitFor(() => expect(toggle).not.toBeChecked())
  },
}

/** Push and bell are separate columns: pushing an event does not touch its bell choice. */
export const TogglePushForAnEvent: Story = {
  play: async ({ canvas, userEvent }) => {
    const push = await canvas.findByRole('checkbox', { name: 'Level up as a push notification' })
    await waitFor(() => expect(push).toBeEnabled())
    await waitFor(() => expect(push).not.toBeChecked())
    await userEvent.click(push)
    await waitFor(() => expect(push).toBeChecked())
    await expect(canvas.getByRole('checkbox', { name: 'Level up in the bell' })).toBeChecked()
  },
}

/** The server only pushes rows it wrote to the bell, so Push is off and locked while Bell is off. */
export const PushNeedsTheBell: Story = {
  play: async ({ canvas, userEvent }) => {
    const bell = await canvas.findByRole('checkbox', { name: 'New chapters available in the bell' })
    const push = canvas.getByRole('checkbox', { name: 'New chapters available as a push notification' })
    await waitFor(() => expect(push).toBeChecked())
    await userEvent.click(bell)
    await waitFor(() => expect(push).toBeDisabled())
    await expect(push).not.toBeChecked()
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

import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api } from '../../../.storybook/mocks/api'
import { PushNotificationsSection } from './PushNotificationsSection'

// Whether the card shows its controls or a fallback depends on the browser the story runs in, so
// the stories assert on what is stable: the heading, and the filter once the controls are there.
const meta = {
  component: PushNotificationsSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 760 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof PushNotificationsSection>

export default meta
type Story = StoryObj<typeof meta>

export const Admin: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Push notifications' })).toBeVisible()
  },
}

export const Reader: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Push notifications' })).toBeVisible()
    await expect(canvas.queryByRole('checkbox', { name: 'Health issue' })).toBeNull()
  },
}

export const NothingPushed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/push/prefs'), () => HttpResponse.json({ types: [] }))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Push notifications' })).toBeVisible()
  },
}

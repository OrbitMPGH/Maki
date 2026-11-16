import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api } from '../../../.storybook/mocks/api'
import { ReadingStatusSection } from './ReadingStatusSection'

const saved = fn()

const withRules = (autoHoldDays: number | null, autoDropDays: number | null) => [
  http.get(api('/settings/reading-status'), () => HttpResponse.json({ autoHoldDays, autoDropDays })),
  http.put(api('/settings/reading-status'), async ({ request }) => {
    const body = await request.json()
    saved(body)
    return HttpResponse.json(body)
  }),
  ...settingsMsw(),
]

const meta = {
  component: ReadingStatusSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: withRules(null, null) },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 760 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ReadingStatusSection>

export default meta
type Story = StoryObj<typeof meta>

export const BothOff: Story = {
  play: async ({ canvas }) => {
    const hold = await canvas.findByRole('switch', { name: 'Put a series on hold when I stop reading it' })
    await waitFor(() => expect(hold).toBeEnabled())
    await expect(hold).not.toBeChecked()
    await expect(canvas.queryByRole('textbox')).toBeNull()
  },
}

export const BothOn: Story = {
  parameters: { msw: withRules(45, 120) },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Days without reading before a series is put on hold' })).toHaveValue('45 days'))
    await expect(canvas.getByRole('textbox', { name: 'Days without reading before a series is dropped' })).toHaveValue('120 days')
  },
}

export const SwitchOnHold: Story = {
  play: async ({ canvas, userEvent }) => {
    const hold = await canvas.findByRole('switch', { name: 'Put a series on hold when I stop reading it' })
    await waitFor(() => expect(hold).toBeEnabled())
    await userEvent.click(hold)
    await waitFor(() => expect(saved).toHaveBeenCalledWith({ autoHoldDays: 30, autoDropDays: null }))
  },
}

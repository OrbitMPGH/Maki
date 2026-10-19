import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { progressSettings, progressSummary } from '../../../.storybook/mocks/settings/data'
import { ProgressSection } from './ProgressSection'

const meta = {
  component: ProgressSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 760 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ProgressSection>

export default meta
type Story = StoryObj<typeof meta>

export const WithGoals: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Every week: 40 chapters/)).toBeVisible()
    await expect(canvas.getByText(/Every month: 2 series finished/)).toBeVisible()
    await expect(canvas.getByRole('switch', { name: /^Track progress and achievements/ })).toBeChecked()
  },
}

export const TrackingOff: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/progress/settings'), () => HttpResponse.json({ ...progressSettings, enabled: false }))),
  },
  play: async ({ canvas }) => {
    const streaks = await canvas.findByRole('switch', { name: /^Show reading streaks/ })
    await expect(streaks).toBeDisabled()
    await expect(canvas.getByRole('button', { name: 'Set goal' })).toBeDisabled()
  },
}

export const NoGoals: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/progress/summary'), () => HttpResponse.json({ ...progressSummary, goals: [] }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Reading goals')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Remove goal' })).toBeNull()
  },
}

export const RemoveAGoal: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const [first] = await canvas.findAllByRole('button', { name: 'Remove goal' })
    await userEvent.click(first)
    const dialog = await body.findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(within(dialog).getByText('Your reading history is not affected. You can set a new goal at any time.')).toBeVisible()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Remove goal' }))
    await waitFor(() => expect(body.queryByRole('dialog')).toBeNull())
  },
}

export const Loading: Story = {
  parameters: { msw: settingsMsw(http.get(api('/progress/settings'), () => neverResolve())) },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('heading', { name: /Progress/ })).toBeNull()
  },
}

export const LoadFailed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/progress/settings'), () => failWith(500))) },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('heading', { name: /Progress/ })).toBeNull()
  },
}

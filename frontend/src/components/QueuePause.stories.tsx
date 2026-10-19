import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { pausedAll, pausedSources } from '../../.storybook/mocks/operations/queue'
import { QueuePauseBanner, QueuePauseButton } from './QueuePause'

const meta = {
  component: QueuePauseButton,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof QueuePauseButton>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Pause downloads' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Pause downloads' })
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'What to pause' })).toBeVisible())
    await userEvent.click(within(dialog).getByRole('button', { name: 'Pause' }))
    await waitFor(() => expect(body.queryByRole('dialog')).not.toBeInTheDocument())
  },
}

export const ChooseResumeTime: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Pause downloads' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Pause downloads' })
    await userEvent.click(within(dialog).getByRole('combobox', { name: 'Resume' }))
    await userEvent.click(await body.findByRole('option', { name: 'Until a time I choose' }))
    await waitFor(() => expect(within(dialog).getByLabelText('Resume at')).toBeVisible())
  },
}

export const PauseFails: Story = {
  parameters: {
    msw: [http.put(api('/queue/pause'), () => HttpResponse.json({ error: 'nope' }, { status: 500 }))],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Pause downloads' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Pause downloads' })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Pause' }))
    await waitFor(async () => expect(await body.findByText("Couldn't pause the downloads")).toBeVisible())
  },
}

export const BannerEverythingPaused: Story = {
  render: () => <QueuePauseBanner canManage />,
  parameters: { msw: [http.get(api('/queue/pause'), () => HttpResponse.json(pausedAll))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Downloads paused')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Resume' })).toBeVisible()
  },
}

export const BannerSourcesPaused: Story = {
  render: () => <QueuePauseBanner canManage />,
  parameters: { msw: [http.get(api('/queue/pause'), () => HttpResponse.json(pausedSources))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Weeb Central paused until/)).toBeVisible()
    await expect(canvas.getByText('Asura Scans paused')).toBeVisible()
    await expect(canvas.getAllByRole('button', { name: 'Resume' })).toHaveLength(2)
  },
}

export const BannerReadOnly: Story = {
  render: () => <QueuePauseBanner canManage={false} />,
  parameters: { msw: [http.get(api('/queue/pause'), () => HttpResponse.json(pausedAll))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Downloads paused')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Resume' })).not.toBeInTheDocument()
  },
}

export const BannerNothingPaused: Story = {
  render: () => <QueuePauseBanner canManage />,
}

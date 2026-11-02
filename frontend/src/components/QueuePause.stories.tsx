import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
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

const onPause = fn()

export const Default: Story = {
  parameters: {
    msw: [
      http.put(api('/queue/pause'), async ({ request }) => {
        onPause(await request.json())
        return HttpResponse.json({ all: { until: null }, sources: [] })
      }),
    ],
  },
  beforeEach: () => onPause.mockClear(),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Pause downloads' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Pause downloads' })
    await waitFor(() => expect(within(dialog).getByRole('combobox', { name: 'What to pause' })).toBeVisible())
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Pause' }))
    await waitFor(() => expect(body.queryByRole('dialog')).not.toBeInTheDocument())
    // The default scope is every source, which the request says by naming none.
    await waitFor(() => expect(onPause).toHaveBeenCalledTimes(1))
    await expect(onPause.mock.calls[0][0]).not.toHaveProperty('source')
  },
}

export const ChooseResumeTime: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Pause downloads' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Pause downloads' })
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Resume' }))
    await userEvent.click(await body.findByRole('option', { name: 'Until a time I choose' }))
    await waitFor(() => expect(within(dialog).getByLabelText('Resume at')).toBeVisible())
  },
}

export const PauseFails: Story = {
  parameters: {
    msw: [http.put(api('/queue/pause'), () => HttpResponse.json({ error: 'nope' }, { status: 500 }))],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Pause downloads' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Pause downloads' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Pause' }))
    await waitFor(async () => expect(await body.findByText("Couldn't pause the downloads")).toBeVisible())
  },
}

export const BannerEverythingPaused: Story = {
  render: () => <QueuePauseBanner canManage />,
  parameters: { msw: [http.get(api('/queue/pause'), () => HttpResponse.json(pausedAll))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Downloads paused')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Resume' })).toBeVisible()
  },
}

export const BannerSourcesPaused: Story = {
  render: () => <QueuePauseBanner canManage />,
  parameters: { msw: [http.get(api('/queue/pause'), () => HttpResponse.json(pausedSources))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Weeb Central paused until/)).toBeVisible()
    await expect(await canvas.findByText('Asura Scans paused')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Resume' })).toHaveLength(2))
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

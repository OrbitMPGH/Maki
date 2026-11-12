import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { Menu } from '@mantine/core'
import { api } from '../../../.storybook/mocks/api'
import { noPause, pausedSeriesAndDisk } from '../../../.storybook/mocks/operations/queue'
import { SeriesPausedButton, SeriesPauseMenuItem } from './SeriesQueuePause'

const meta = {
  component: SeriesPausedButton,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: { seriesId: 9 },
} satisfies Meta<typeof SeriesPausedButton>

export default meta
type Story = StoryObj<typeof meta>

const onResume = fn()
const onPause = fn()

export const Paused: Story = {
  parameters: {
    msw: [
      http.get(api('/queue/pause'), () => HttpResponse.json(pausedSeriesAndDisk)),
      http.delete(api('/queue/pause/series/:id'), ({ params }) => {
        onResume(Number(params.id))
        return HttpResponse.json(noPause)
      }),
    ],
  },
  beforeEach: () => onResume.mockClear(),
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Resume downloads' }))
    await waitFor(() => expect(onResume).toHaveBeenCalledWith(9))
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'Resume downloads' })).toBeNull())
  },
}

export const Running: Story = {
  play: async ({ canvas }) => {
    await new Promise((resolve) => setTimeout(resolve, 200))
    await expect(canvas.queryByRole('button', { name: 'Resume downloads' })).toBeNull()
  },
}

export const ReaderSeesNoMenuItem: Story = {
  parameters: { auth: 'reader' },
  render: () => (
    <Menu opened>
      <Menu.Target>
        <button type="button">Download options</button>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item>Next 10 chapters</Menu.Item>
        <SeriesPauseMenuItem seriesId={9} />
      </Menu.Dropdown>
    </Menu>
  ),
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('menuitem', { name: 'Next 10 chapters' })).toBeVisible()
    await expect(body.queryByRole('menuitem', { name: 'Pause downloads for this series' })).toBeNull()
  },
}

export const MenuItemPauses: Story = {
  parameters: {
    msw: [
      http.put(api('/queue/pause/series'), async ({ request }) => {
        onPause(await request.json())
        return HttpResponse.json(pausedSeriesAndDisk)
      }),
    ],
  },
  beforeEach: () => onPause.mockClear(),
  render: () => (
    <Menu opened>
      <Menu.Target>
        <button type="button">Download options</button>
      </Menu.Target>
      <Menu.Dropdown>
        <SeriesPauseMenuItem seriesId={9} />
      </Menu.Dropdown>
    </Menu>
  ),
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('menuitem', { name: 'Pause downloads for this series' }))
    await waitFor(() => expect(onPause).toHaveBeenCalledWith({ seriesId: 9 }))
  },
}

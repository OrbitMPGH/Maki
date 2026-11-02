import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { awaitingImportItem, queueItems, queuePage } from '../../.storybook/mocks/operations/queue'
import type { QueueSummaryDto } from '../api/types'
import { ActivityButton } from './ActivityButton'

const withQueue = (summary: QueueSummaryDto, items = queueItems) => [
  http.get(api('/queue/summary'), () => HttpResponse.json(summary)),
  http.get(api('/queue'), () => HttpResponse.json(queuePage(items))),
]

const meta = {
  component: ActivityButton,
  tags: ['ai-generated'],
  parameters: { layout: 'centered', msw: withQueue({ active: 9, awaitingImport: 0, failed: 2 }) },
  decorators: [
    // The dropdown opens below and to the left of the icon, so leave it somewhere to go.
    (Story) => (
      <div style={{ minHeight: 560, minWidth: 400, display: 'flex', justifyContent: 'flex-end', alignItems: 'flex-start' }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ActivityButton>

export default meta
type Story = StoryObj<typeof meta>

export const Downloading: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Activity, 9 downloads in progress' }))
    // The popover renders in a portal on document.body.
    const body = within(canvasElement.ownerDocument.body)
    const dropdown = await body.findByRole('dialog', { name: 'Activity' })
    await waitFor(() => expect(within(dropdown).getAllByText('Lanterns Over Kasumi').length).toBeGreaterThan(0))
    await expect(await within(dropdown).findByText('14/22')).toBeVisible()
    await expect(await within(dropdown).findByText(/^and \d+ more$/)).toBeVisible()

    await userEvent.click(await within(dropdown).findByRole('button', { name: 'Open Activity' }))
    await waitFor(() => expect(body.queryByRole('dialog', { name: 'Activity' })).toBeNull())
  },
}

export const WaitingForImport: Story = {
  parameters: { msw: withQueue({ active: 0, awaitingImport: 1, failed: 0 }, [awaitingImportItem]) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(
      await canvas.findByRole('button', { name: 'Activity, 1 download waiting for an import decision' }),
    )
    const dropdown = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Activity' })
    await expect(await within(dropdown).findByText('Needs review')).toBeVisible()
  },
}

export const Idle: Story = {
  parameters: { msw: withQueue({ active: 0, awaitingImport: 0, failed: 3 }, []) },
  play: async ({ canvas }) => {
    // Nothing in flight: the icon is the old plain link and opens no dropdown.
    await waitFor(async () =>
      expect(await canvas.findByRole('link', { name: 'Activity' })).toHaveAttribute('href', '/activity'),
    )
    await expect(canvas.queryByRole('button', { name: /activity/i })).toBeNull()
  },
}

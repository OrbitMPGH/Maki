import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { ownRequests, requestsFor } from '../../.storybook/mocks/operations/requests'
import RequestsPage from './RequestsPage'

const meta = {
  component: RequestsPage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', router: { path: '/requests', initialEntries: ['/requests'] } },
} satisfies Meta<typeof RequestsPage>

export default meta
type Story = StoryObj<typeof meta>

const readerRequests = http.get(api('/requests'), ({ request }) =>
  HttpResponse.json(requestsFor(new URL(request.url).searchParams.get('status'), ownRequests)),
)
const noRequests = http.get(api('/requests'), () => HttpResponse.json([]))

export const PendingQueue: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Starlit Ferry')).toBeVisible()
    await waitFor(() => expect((canvas.getAllByRole('button', { name: 'Approve' })).length).toBeGreaterThan(1))
    await expect(await canvas.findByText('Adjusted by Maki, asked for all chapters')).toBeVisible()
    await expect(await canvas.findByText('Ch. 151–203')).toBeVisible()
  },
}

export const ResolvedTab: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('tab', { name: 'Resolved' }))
    await expect(await canvas.findByText('Approved by Maki, queued 11 chapters')).toBeVisible()
    await expect(await canvas.findByText('Rejected by Maki · Not available from any enabled source')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
  },
}

export const AllTab: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('tab', { name: 'All' }))
    await expect(await canvas.findByText('Tidewater Almanac')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByText('Approval in progress').length).toBeGreaterThan(0))
  },
}

export const ApproveNewSeries: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Approve' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Approve request' })
    // The folder list can land after the dialog opens, so pick one explicitly.
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Root folder' }))
    await userEvent.click(await body.findByRole('option', { name: '/library/manhwa' }))
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Approve and queue' }))
    await waitFor(async () => expect(await body.findByText('Approved, queued 24 chapters')).toBeVisible())
  },
}

export const ApproveChapterRequest: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Approve' }))[1])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Approve request' })
    await expect(within(dialog).queryByRole('combobox', { name: 'Root folder' })).not.toBeInTheDocument()
    await waitFor(() => expect(within(dialog).getByText(/Ashen Crown Regression: Ch. 151–203/)).toBeVisible())
  },
}

export const ApproveFails: Story = {
  parameters: {
    msw: [http.post(api('/requests/:id/approve'), () => failWith(409, 'This request was already handled by someone else'))],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Approve' }))[1])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Approve request' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Approve and queue' }))
    await expect(await canvas.findByText('This request was already handled by someone else')).toBeVisible()
  },
}

export const RejectWithNote: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Reject' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Reject request' })
    await userEvent.type(await within(dialog).findByLabelText('Reason (optional)'), 'Not something we host')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Reject' }))
    await waitFor(async () => expect(await body.findByText('Request rejected')).toBeVisible())
  },
}

export const EditChapterRange: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Edit request for Ashen Crown Regression' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Edit request' })
    await userEvent.clear(await within(dialog).findByLabelText('To'))
    await userEvent.type(await within(dialog).findByLabelText('To'), '180')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Save range' }))
    await waitFor(async () => expect(await body.findByText('Now ch. 151–180')).toBeVisible())
  },
}

export const DeleteConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Remove request' }))[0])
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Delete this request?' })
    await waitFor(() => expect(within(dialog).getByText("The request for Starlit Ferry is removed. This can't be undone.")).toBeVisible())
  },
}

export const NoPendingRequests: Story = {
  parameters: { msw: [noRequests] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No pending requests')).toBeVisible()
  },
}

export const NothingResolved: Story = {
  parameters: { msw: [noRequests] },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('tab', { name: 'Resolved' }))
    await expect(await canvas.findByText('Nothing here')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/requests'), () => neverResolve())] },
}

export const LoadFailed: Story = {
  parameters: { msw: [http.get(api('/requests'), () => failWith(500, 'The requests could not be loaded'))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Couldn't load requests")).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

export const ReaderOwnRequests: Story = {
  parameters: { auth: 'reader', msw: [readerRequests] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Series and chapters you have asked an admin for.')).toBeVisible()
    await expect(await canvas.findByText('Starlit Ferry')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
    await expect(canvas.queryByRole('button', { name: /Edit request for/ })).not.toBeInTheDocument()
    await waitFor(() => expect(canvas.getAllByRole('button', { name: 'Remove request' }).length).toBeGreaterThan(0))
  },
}

export const ReaderResolvedRequests: Story = {
  parameters: { auth: 'reader', msw: [readerRequests] },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('tab', { name: 'Resolved' }))
    await expect(await canvas.findByText('Approved by Maki, queued 11 chapters')).toBeVisible()
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'Remove request' })).not.toBeInTheDocument())
  },
}

export const ReaderNoRequests: Story = {
  parameters: { auth: 'reader', msw: [noRequests] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Search for a series and request it - it will show up here once you do.')).toBeVisible()
  },
}

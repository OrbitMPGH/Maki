import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../../.storybook/mocks/api'
import { continueReading } from '../../../.storybook/mocks/home'
import { ReadingCardMenu } from './ReadingCardMenu'

const meta = {
  component: ReadingCardMenu,
  tags: ['ai-generated'],
  args: { item: continueReading[0], rail: 'continue', className: '', variant: 'default', size: 36 },
  parameters: { layout: 'centered' },
} satisfies Meta<typeof ReadingCardMenu>

export default meta
type Story = StoryObj<typeof meta>

const TRIGGER = 'More options for Lanterns Over Kasumi'
const hideRequested = fn()

export const Default: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: TRIGGER }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(() => expect(body.getByRole('menuitem', { name: 'Open series' })).toBeVisible())
    await expect(body.getByRole('menuitem', { name: 'Remove from Continue reading' })).toBeVisible()
  },
}

export const OnJumpBackIn: Story = {
  args: { rail: 'jumpback' },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: TRIGGER }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(() => expect(body.getByRole('menuitem', { name: 'Remove from Jump back in' })).toBeVisible())
  },
}

export const FloatingOverCover: Story = {
  args: { variant: 'filled', size: 28 },
}

export const RemoveShowsUndo: Story = {
  parameters: {
    msw: [
      http.post(api('/home/reading/:seriesId/hide'), ({ params }) => {
        hideRequested(String(params.seriesId))
        return new HttpResponse(null, { status: 204 })
      }),
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(canvas.getByRole('button', { name: TRIGGER }))
    await userEvent.click(await body.findByRole('menuitem', { name: 'Remove from Continue reading' }))
    await expect(await body.findByText(/Removed Lanterns Over Kasumi/)).toBeVisible()
    await waitFor(() => expect(hideRequested).toHaveBeenCalledWith('1'))
    await userEvent.click(body.getByRole('button', { name: 'Undo' }))
    await waitFor(() => expect(body.queryByRole('button', { name: 'Undo' })).toBeNull())
  },
}

export const RemoveFails: Story = {
  parameters: { msw: [http.post(api('/home/reading/:seriesId/hide'), () => failWith(500, 'Database is locked'))] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(canvas.getByRole('button', { name: TRIGGER }))
    await userEvent.click(await body.findByRole('menuitem', { name: 'Remove from Continue reading' }))
    await expect(await body.findByText(/Could not remove Lanterns Over Kasumi/)).toBeVisible()
  },
}

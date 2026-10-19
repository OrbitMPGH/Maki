import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { relinkPlan, seriesMsw } from '../../.storybook/mocks/series'
import { RelinkFilesModal } from './RelinkFilesModal'

const planning = (plan: ReturnType<typeof relinkPlan>) => seriesMsw(http.post(api('/series/:id/relink/plan'), () => HttpResponse.json(plan)))

const meta = {
  component: RelinkFilesModal,
  tags: ['ai-generated'],
  args: { seriesId: 1, opened: true, onClose: fn() },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof RelinkFilesModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogOf = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Relink files' })

export const Plan: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByRole('checkbox', { name: /Trust .*v01/ })).toBeChecked()
  },
}

export const ExcludeAFile: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    const box = await within(dialog).findByRole('checkbox', { name: /Trust .*v01/ })
    await userEvent.click(box)
    await waitFor(() => expect(box).not.toBeChecked())
  },
}

export const NothingToChange: Story = {
  parameters: {
    msw: planning({ ...relinkPlan(1), files: [], chapters: [], moved: 0, supersededCount: 0, supersededBytes: 0, unrecognized: 0 }),
  },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText(/already on the best file available/)).toBeVisible()
  },
}

export const Planning: Story = {
  parameters: { msw: seriesMsw(http.post(api('/series/:id/relink/plan'), () => neverResolve())) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText(/Reading the folder and every volume archive/)).toBeVisible()
  },
}

export const PlanFails: Story = {
  parameters: { msw: seriesMsw(http.post(api('/series/:id/relink/plan'), () => failWith(500, 'The folder could not be read'))) },
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(await within(dialog).findByText('Could not build a plan for this series.')).toBeVisible()
  },
}

export const ReaderCannotDeleteLeftovers: Story = {
  parameters: { auth: 'reader', msw: seriesMsw() },
  play: async ({ canvasElement }) => {
    await dialogOf(canvasElement)
  },
}

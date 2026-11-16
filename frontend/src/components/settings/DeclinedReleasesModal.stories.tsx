import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith } from '../../../.storybook/mocks/api'
import { DeclinedReleasesModal } from './DeclinedReleasesModal'

const meta = {
  component: DeclinedReleasesModal,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn() },
  parameters: { msw: settingsMsw() },
} satisfies Meta<typeof DeclinedReleasesModal>

export default meta
type Story = StoryObj<typeof meta>

const modalOf = async (canvasElement: HTMLElement) =>
  within(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Declined releases' }))

const onUnblock = fn()

export const Default: Story = {
  parameters: {
    msw: settingsMsw(
      http.post(api('/upgrades/proposals/:id/unblock'), ({ params }) => {
        onUnblock(Number(params.id))
        return new HttpResponse(null, { status: 204 })
      }),
    ),
  },
  beforeEach: () => onUnblock.mockClear(),
  play: async ({ canvasElement, userEvent }) => {
    const modal = await modalOf(canvasElement)
    await expect(await modal.findByText('The Ninth Kitchen v14 (2024) (Digital) (Saltcellar)')).toBeVisible()
    await expect(modal.getByRole('link', { name: 'The Ninth Kitchen' })).toHaveAttribute('href', '/series/2')
    await userEvent.click(modal.getByRole('button', { name: 'Allow The Ninth Kitchen v14 (2024) (Digital) (Saltcellar) again' }))
    await waitFor(() => expect(onUnblock).toHaveBeenCalledWith(42))
  },
}

export const Empty: Story = {
  parameters: { msw: settingsMsw(http.get(api('/upgrades/proposals'), () => HttpResponse.json([]))) },
  play: async ({ canvasElement }) => {
    const modal = await modalOf(canvasElement)
    await expect(await modal.findByText('No declined releases')).toBeVisible()
  },
}

export const UnblockRefused: Story = {
  parameters: {
    msw: settingsMsw(http.post(api('/upgrades/proposals/:id/unblock'), () => failWith(409, 'That proposal was already resolved.'))),
  },
  play: async ({ canvasElement, userEvent }) => {
    const modal = await modalOf(canvasElement)
    await userEvent.click(await modal.findByRole('button', { name: /^Allow Lanterns Over Kasumi/ }))
    await expect(await modal.findByText('That proposal was already resolved.')).toBeVisible()
  },
}

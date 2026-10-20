import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { UpcomingView } from './UpcomingView'

const meta = {
  component: UpcomingView,
  tags: ['ai-generated'],
} satisfies Meta<typeof UpcomingView>

export default meta
type Story = StoryObj<typeof meta>

export const Timeline: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect((canvas.getAllByText('Lanterns Over Kasumi')).length).toBeGreaterThan(0))
    await expect(await canvas.findByText('Running late')).toBeVisible()
    await expect(await canvas.findByText('Further out')).toBeVisible()
    await waitFor(() => expect(canvas.getAllByLabelText(/^Likely /).length).toBeGreaterThan(0))
    await waitFor(() => expect(canvas.getAllByLabelText(/MANGA Plus has it scheduled for/).length).toBeGreaterThan(0))
  },
}

export const BarExplainsItsConfidence: Story = {
  play: async ({ canvas }) => {
    const [bar] = await canvas.findAllByLabelText(/^Likely /)
    await userEvent.hover(bar)
    await expect(
      await within(document.body).findByText(/came out inside their window/),
    ).toBeVisible()
  },
}

export const Phone: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvas }) => {
    await waitFor(() => expect((canvas.getAllByText('Lanterns Over Kasumi')).length).toBeGreaterThan(0))
  },
}

export const NothingToPlace: Story = {
  parameters: { msw: [http.get(api('/upcoming'), () => HttpResponse.json([]))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing to place yet')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/upcoming'), neverResolve)] },
}

export const LoadFailed: Story = {
  parameters: { msw: [http.get(api('/upcoming'), () => failWith(500))] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText("Couldn't load this section")).toBeVisible()
  },
}

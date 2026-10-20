import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import {
  discoverPage,
  indexStatusBuilding,
  indexStatusMissing,
  indexStatusOff,
  indexStatusReady,
} from '../../.storybook/mocks/discover'
import { RecommendationModelSwitch } from './RecommendationModelSwitch'

const meta = {
  component: RecommendationModelSwitch,
  tags: ['ai-generated'],
  args: { status: indexStatusReady, onSelect: fn(), busy: false },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 520 }}>
        <Story />
      </div>
    ),
  ],
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof RecommendationModelSwitch>

export default meta
type Story = StoryObj<typeof meta>

export const Ready: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: /Semantic search and recommendations/ })).toBeChecked())
    await expect(await canvas.findByText(/series embedded/)).toBeVisible()
  },
}

export const SwitchOff: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('switch'))
    await waitFor(() => expect(args.onSelect).toHaveBeenCalledWith('off'))
  },
}

export const Off: Story = {
  args: { status: indexStatusOff },
  play: async ({ args, canvas, userEvent }) => {
    const toggle = await canvas.findByRole('switch')
    await waitFor(() => expect(toggle).not.toBeChecked())
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Check for prebuilt now' })).toBeDisabled())
    await userEvent.click(toggle)
    await waitFor(() => expect(args.onSelect).toHaveBeenCalledWith('base'))
  },
}

export const Indexing: Story = {
  args: { status: indexStatusBuilding },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Indexing/)).toBeVisible()
    await expect(await canvas.findByRole('progressbar')).toBeVisible()
  },
}

export const SwitchingModel: Story = {
  args: {
    status: { ...indexStatusReady, modelSwitching: true, vectorCount: 0 },
    busy: true,
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Switching model/)).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('switch')).toBeDisabled())
  },
}

export const WaitingForDump: Story = {
  args: { status: { ...indexStatusMissing, dumpPresent: false } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Waiting for the MangaBaka snapshot/)).toBeVisible()
  },
}

export const NoIndexYet: Story = {
  args: { status: { ...indexStatusMissing, lastError: null } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/prebuilt vectors download automatically/)).toBeVisible()
  },
}

export const WithErrors: Story = {
  args: {
    status: { ...indexStatusMissing, modelSwitchError: 'No prebuilt index exists for this model yet.' },
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Model switch: No prebuilt index/)).toBeVisible()
    await expect(await canvas.findByText(/Last error: The prebuilt index was built for a different model/)).toBeVisible()
  },
}

export const StatusLoading: Story = {
  args: { status: undefined },
}

export const CustomDescription: Story = {
  args: { description: 'Needed before Discover can suggest anything.' },
}

export const CheckForPrebuilt: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Check for prebuilt now' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText(/Downloaded .* embedded series/)).toBeVisible()
  },
}

export const PrebuiltAlreadyCurrent: Story = {
  parameters: {
    msw: discoverPage(
      http.post(api('/settings/recommendations/prebuilt/download'), () =>
        HttpResponse.json({ installed: false, reason: 'The installed index is already current.', rowCount: null }),
      ),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Check for prebuilt now' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText('The installed index is already current.')).toBeVisible()
  },
}

export const PrebuiltDownloadFails: Story = {
  parameters: {
    msw: discoverPage(http.post(api('/settings/recommendations/prebuilt/download'), () => failWith(502, 'The release server did not answer'))),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Check for prebuilt now' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Check for prebuilt now' })).toBeEnabled())
  },
}

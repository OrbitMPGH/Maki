import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../../.storybook/mocks/api'
import { seriesMsw, torrentProposal } from '../../../.storybook/mocks/series'
import { TorrentProposalCard } from './TorrentProposalCard'

const meta = {
  component: TorrentProposalCard,
  tags: ['ai-generated'],
  args: { proposal: torrentProposal(1) },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof TorrentProposalCard>

export default meta
type Story = StoryObj<typeof meta>

export const Panel: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: 'Lanterns Over Kasumi' })).toHaveAttribute(
      'href',
      '/series/1?tab=chapters',
    )
    await expect(await canvas.findByRole('button', { name: 'Grab' })).toBeEnabled()
  },
}

export const Banner: Story = {
  args: { banner: true },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('link', { name: 'Lanterns Over Kasumi' })).toBeNull()
  },
}

export const WithWarnings: Story = {
  args: {
    proposal: torrentProposal(3, {
      reasons: ['title_uncertain', 'whole_series_pack', 'over_budget'],
      tier: 'scanlator',
      span: { volumeStart: null, volumeEnd: null, chapters: [], wholeSeries: true },
      sizeBytes: 6_400_000_000,
      missingCount: 12,
      unknownCount: 4,
    }),
  },
}

export const AlreadyDownloading: Story = {
  args: { proposal: torrentProposal(1, { queueItemId: 9100 }) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Grab' })).toBeDisabled()
  },
}

export const GrabSendsToClient: Story = {
  parameters: {
    msw: seriesMsw(http.post(api('/upgrades/proposals/:id/grab'), () => HttpResponse.json({ queueItemId: 9100 }))),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Grab' }))
    // The toast renders in the Notifications portal on document.body.
    await expect(await within(canvasElement.ownerDocument.body).findByText(/Sent to the download client/)).toBeVisible()
  },
}

export const DismissFails: Story = {
  parameters: {
    msw: seriesMsw(http.post(api('/upgrades/proposals/:id/dismiss'), () => failWith(409, 'This proposal was already resolved'))),
  },
  args: { proposal: torrentProposal(1) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Dismiss' }))
  },
}

export const ReaderCannotDecide: Story = {
  parameters: { auth: 'reader', msw: seriesMsw() },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Northwind/)).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Grab' })).toBeNull()
  },
}

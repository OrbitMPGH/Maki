import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../.storybook/mocks/settings'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { importListsStatus } from '../../.storybook/mocks/settings/data'
import { ImportListsSection } from './ImportListsSection'

const meta = {
  component: ImportListsSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 860 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ImportListsSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Cloudless Archipelago')).toBeVisible()
    await expect(canvas.getByText('Connect MyAnimeList on the Scrobble page to import from it.')).toBeVisible()
  },
}

export const ConnectedTrackerControls: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('textbox', { name: 'Max per run' })).toHaveValue('25')
    await expect(canvas.getByRole('button', { name: 'Run now' })).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Sync everything' })).toBeVisible()
  },
}

export const SyncEverythingConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Sync everything' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(within(dialog).getByText('Sync everything from AniList?')).toBeVisible()
  },
}

export const RunNow: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Run now' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Run now' })).toBeEnabled())
  },
}

export const ReaderFilesRequests: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Entries are filed as requests for an admin to approve.')).toBeVisible()
  },
}

export const NothingSkipped: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/importlists'), () => HttpResponse.json({ ...importListsStatus, skipped: [] }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing unmatched.')).toBeVisible()
  },
}

export const TurnedOffForInstance: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/importlists'), () => HttpResponse.json({ ...importListsStatus, enabled: false }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Import lists are turned off for this instance.')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: settingsMsw(http.get(api('/importlists'), () => neverResolve())) },
}

export const LoadFailed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/importlists'), () => failWith(500))) },
}

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
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText(/^Not connected: .*MyAnimeList/)).toBeVisible()
    await expect(canvas.getByRole('link', { name: 'Connect one on the Scrobble page' })).toHaveAttribute('href', '/scrobble')
    // Unmatched entries open in a modal from their count.
    await expect(canvas.queryByText('Cloudless Archipelago')).toBeNull()
    await userEvent.click(await canvas.findByRole('button', { name: 'Review' }))
    const modal = within(await within(document.body).findByRole('dialog', { name: 'Not matched or ignored' }))
    await expect(await modal.findByText('Cloudless Archipelago')).toBeVisible()
  },
}

export const ConnectedTrackerControls: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^AniList/ }))
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Max per run' })).toHaveValue('25'))
    await expect(await canvas.findByRole('button', { name: 'Run now' })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Sync everything' })).toBeVisible()
  },
}

export const SyncEverythingConfirm: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^AniList/ }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Sync everything' }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(await within(dialog).findByText('Sync everything from AniList?')).toBeVisible()
  },
}

export const RunNow: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^AniList/ }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Run now' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Run now' })).toBeEnabled())
  },
}

export const ReaderFilesRequests: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^AniList/ }))
    await expect(await canvas.findByText('Entries are filed as requests for an admin to approve.')).toBeVisible()
  },
}

let lastEntryRetried = false

export const ResolvingTheLastEntryClosesTheReview: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/importlists'), () =>
        HttpResponse.json({
          ...importListsStatus,
          skipped: lastEntryRetried ? [] : importListsStatus.skipped.filter((s) => s.reason === 'Unmatched').slice(0, 1),
        }),
      ),
      http.delete(api('/importlists/skipped/:id'), () => {
        lastEntryRetried = true
        return new HttpResponse(null, { status: 204 })
      }),
    ),
  },
  beforeEach: () => {
    lastEntryRetried = false
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Review' }))
    const modal = within(await within(document.body).findByRole('dialog', { name: 'Not matched or ignored' }))
    await userEvent.click(await modal.findByRole('button', { name: 'Retry' }))
    await waitFor(() => expect(within(document.body).queryByRole('dialog', { name: 'Not matched or ignored' })).toBeNull())
    await waitFor(() => expect(canvas.queryByRole('button', { name: 'Review' })).toBeNull())
  },
}

export const NothingSkipped: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/importlists'), () => HttpResponse.json({ ...importListsStatus, skipped: [] }))),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: /^AniList/ })).toBeVisible())
    await expect(canvas.queryByText('Not matched or ignored')).toBeNull()
    await expect(canvas.queryByRole('button', { name: 'Review' })).toBeNull()
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

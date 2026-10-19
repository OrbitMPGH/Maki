import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, neverResolve } from '../../../.storybook/mocks/api'
import { opdsOff, opdsOn, readerSettings } from '../../../.storybook/mocks/settings/data'
import { KavitaSyncSection, OpdsSection } from './ReadingCards'

const meta = {
  component: KavitaSyncSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 820 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof KavitaSyncSection>

export default meta
type Story = StoryObj<typeof meta>

const reader = (patch: Partial<typeof readerSettings>) =>
  http.get(api('/settings/reader'), () => HttpResponse.json({ ...readerSettings, ...patch }))

export const KavitaSyncConnected: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Connected to Kavita.')).toBeVisible()
    await expect(canvas.getByRole('switch', { name: /^Mark chapters read in Kavita too/ })).toBeChecked()
    await expect(await canvas.findByText(/912 chapters marked read across 38 series/)).toBeVisible()
  },
}

export const KavitaSyncBelongsToAnotherAccount: Story = {
  parameters: { msw: settingsMsw(reader({ kavitaUserId: 7 })) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/belongs to another Maki account/)).toBeVisible()
    await expect(canvas.getByRole('switch', { name: /^Mark chapters read in Kavita too/ })).toBeDisabled()
    await expect(canvas.queryByRole('button', { name: 'Import read status' })).toBeNull()
  },
}

export const KavitaSyncNotAdminKey: Story = {
  parameters: { msw: settingsMsw(reader({ kavitaLive: 'NotAdmin' })) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Kavita only sends reading updates to admin accounts/)).toBeVisible()
  },
}

export const KavitaSyncUnreachable: Story = {
  parameters: { msw: settingsMsw(reader({ kavitaLive: 'Unreachable' })) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Can't reach Kavita right now/)).toBeVisible()
  },
}

export const KavitaSyncConnecting: Story = {
  parameters: { msw: settingsMsw(reader({ kavitaLive: 'Connecting' })) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Connecting to Kavita…')).toBeVisible()
  },
}

export const KavitaImportRunning: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/reader/import/kavita'), () =>
        HttpResponse.json({ running: true, finishedAt: null, result: null, error: null }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Reading progress from Kavita…')).toBeVisible()
  },
}

export const KavitaImportFailed: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/reader/import/kavita'), () =>
        HttpResponse.json({
          running: false,
          finishedAt: '2026-10-18T20:00:00Z',
          result: {
            seriesMatched: 30,
            chaptersMarked: 410,
            seriesUnmatched: 2,
            seriesFailed: 7,
            failedTitles: ['Lanterns Over Kasumi', 'The Ninth Kitchen', 'Iron Petal Academy', 'Snowfield Letters', 'Bluefin Boys', 'Clockwork Tea House', 'Neon Shrine Maiden'],
          },
          error: null,
        }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Could not read progress for/)).toBeVisible()
    await expect(canvas.getByText(/\+2 more/)).toBeVisible()
  },
}

export const KavitaSyncHiddenWithoutKavita: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/reader/used'), () => HttpResponse.json({ used: true, kavita: false }))),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.queryByRole('heading', { name: 'Kavita read sync' })).toBeNull())
  },
}

export const OpdsEnabled: Story = {
  render: () => <OpdsSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('a91f3c…')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Regenerate' })).toBeVisible()
    await expect(canvas.getByRole('switch', { name: /^Track reading progress from OPDS/ })).toBeChecked()
  },
}

export const OpdsDisabled: Story = {
  render: () => <OpdsSection />,
  parameters: { msw: settingsMsw(http.get(api('/settings/opds'), () => HttpResponse.json(opdsOff))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('switch', { name: /^Enable the OPDS catalogue/ })).not.toBeChecked()
    await expect(canvas.queryByText('Feed URL')).toBeNull()
  },
}

export const OpdsFirstEnableShowsFeedUrlOnce: Story = {
  render: () => <OpdsSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/opds'), () => HttpResponse.json(opdsOff)),
      http.put(api('/settings/opds'), () =>
        HttpResponse.json({ ...opdsOn, tokenPrefix: 'd4e8b1', feedUrl: '/api/v1/opds/d4e8b1aa90c25f73e6b0' }),
      ),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('switch', { name: /^Enable the OPDS catalogue/ }))
    const dialog = await body.findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await userEvent.type(within(dialog).getByLabelText('Your password'), 'correct horse')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Enable' }))
    await expect(await canvas.findByText(/Copy this now/)).toBeVisible()
    await expect(canvas.getByText(/\/api\/v1\/opds\/d4e8b1aa90c25f73e6b0/)).toBeVisible()
  },
}

export const OpdsRegenerate: Story = {
  render: () => <OpdsSection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('button', { name: 'Regenerate' }))
    const dialog = await body.findByRole('dialog')
    await waitFor(() => expect(dialog).toBeVisible())
    await expect(within(dialog).getByRole('button', { name: 'Regenerate' })).toBeDisabled()
    await userEvent.type(within(dialog).getByLabelText('Your password'), 'correct horse')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Regenerate' }))
    await expect(await canvas.findByText(/f07be2c19a44d85e7a30/)).toBeVisible()
  },
}

export const OpdsLoading: Story = {
  render: () => <OpdsSection />,
  parameters: { msw: settingsMsw(http.get(api('/settings/opds'), () => neverResolve())) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('switch', { name: /^Enable the OPDS catalogue/ })).not.toBeChecked()
  },
}

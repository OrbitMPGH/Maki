import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { upgradeProfiles } from '../../../.storybook/mocks/series'
import { SeriesActionsMenu } from './SeriesActionsMenu'

const meta = {
  component: SeriesActionsMenu,
  tags: ['ai-generated'],
  args: {
    monitorMode: 'All',
    incognito: 'Off',
    notificationMode: 'Default',
    upgradeProfileId: null,
    upgradeProfiles,
    readFileCleanup: 'Default',
    readFileCleanupActive: false,
    canEdit: true,
    canCleanUpReadFiles: true,
    canScanUpgrades: true,
    scanningUpgrades: false,
    searchingVolumes: false,
    busy: false,
    canArchive: true,
    archived: false,
    canRemove: true,
    onRefreshChapters: fn(),
    onRefreshMetadata: fn(),
    onEditMetadata: fn(),
    onRescan: fn(),
    onMove: fn(),
    onRename: fn(),
    onSetMonitor: fn(),
    onSetIncognito: fn(),
    onSetNotify: fn(),
    onSetUpgradeProfile: fn(),
    onSetReadFileCleanup: fn(),
    onScanUpgrades: fn(),
    onSearchVolumes: fn(),
    onArchive: fn(),
    onRestore: fn(),
    onRemove: fn(),
  },
  decorators: [
    (Story) => (
      <div style={{ display: 'flex', justifyContent: 'flex-end', minHeight: 520 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof SeriesActionsMenu>

export default meta
type Story = StoryObj<typeof meta>

const body = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

export const Admin: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    await userEvent.click(await within(menu).findByRole('menuitem', { name: 'Refresh chapters' }))
    await expect(args.onRefreshChapters).toHaveBeenCalledOnce()
  },
}

export const PickMonitorMode: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    await userEvent.hover(await within(menu).findByRole('menuitem', { name: /monitor/i }))
    const smart = await body(canvasElement).findByRole('menuitemradio', { name: /^Smart/ })
    smart.focus()
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(args.onSetMonitor).toHaveBeenCalledWith('Smart'))
  },
}

export const Remove: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Remove from library' }))
    await expect(args.onRemove).toHaveBeenCalledOnce()
  },
}

export const ReaderPermissions: Story = {
  args: { canEdit: false, canCleanUpReadFiles: false, canScanUpgrades: false, canArchive: false, canRemove: false },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    await expect(within(menu).queryByRole('menuitem', { name: 'Edit metadata' })).toBeNull()
    await expect(within(menu).queryByRole('menuitem', { name: /remove/i })).toBeNull()
  },
}

export const ScanRunning: Story = {
  args: { scanningUpgrades: true, searchingVolumes: true },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
  },
}

export const ProfilePinnedAndCleanupOn: Story = {
  args: { upgradeProfileId: 2, readFileCleanup: 'On', readFileCleanupActive: true, incognito: 'ScrobbleOnly', notificationMode: 'Muted', monitorMode: 'MainOnly' },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
  },
}

export const OfflineSaving: Story = {
  args: { offlineSaved: 12, onSaveUnreadOffline: fn(), onRemoveOffline: fn() },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
  },
}

export const Busy: Story = {
  args: { busy: true },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: 'More actions' })).toBeDisabled())
  },
}

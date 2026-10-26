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
    pageConversion: 'Default',
    canSetPageConversion: true,
    canEdit: true,
    canCleanUpReadFiles: true,
    canScanUpgrades: true,
    scanningUpgrades: false,
    searchingVolumes: false,
    busy: false,
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
    onSetPageConversion: fn(),
    onScanUpgrades: fn(),
    onSearchVolumes: fn(),
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
  args: { canEdit: false, canCleanUpReadFiles: false, canSetPageConversion: false, canScanUpgrades: false, canRemove: false },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    await expect(within(menu).queryByRole('menuitem', { name: 'Edit metadata' })).toBeNull()
    await expect(within(menu).queryByRole('menuitem', { name: /remove/i })).toBeNull()
    await expect(within(menu).queryByRole('menuitem', { name: /Page conversion/ })).toBeNull()
  },
}

export const PickPageConversion: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    const item = await within(menu).findByRole('menuitem', { name: /Page conversion/ })
    await expect(item).toHaveTextContent('Rules')
    await userEvent.hover(item)
    const follow = await body(canvasElement).findByRole('menuitemradio', { name: 'Follow the rules' })
    await expect(follow).toHaveAttribute('aria-checked', 'true')
    for (const name of ['Never', 'Smallest', 'Compact', 'Balanced', 'High', 'Lossless']) {
      await expect(body(canvasElement).getByRole('menuitemradio', { name })).toBeVisible()
    }
    const balanced = body(canvasElement).getByRole('menuitemradio', { name: 'Balanced' })
    balanced.focus()
    await userEvent.keyboard('{Enter}')
    await waitFor(() => expect(args.onSetPageConversion).toHaveBeenCalledWith('Balanced'))
  },
}

export const PageConversionNever: Story = {
  args: { pageConversion: 'Never' },
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    const item = await within(menu).findByRole('menuitem', { name: /Page conversion/ })
    await expect(item).toHaveTextContent('Never')
    await userEvent.hover(item)
    await expect(await body(canvasElement).findByRole('menuitemradio', { name: 'Never' })).toHaveAttribute('aria-checked', 'true')
    await expect(args.onSetPageConversion).not.toHaveBeenCalled()
  },
}

export const PageConversionFixedStop: Story = {
  args: { pageConversion: 'Lossless' },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    await expect(await within(menu).findByRole('menuitem', { name: /Page conversion/ })).toHaveTextContent('Lossless')
  },
}

export const UndoConversion: Story = {
  args: { onUndoConversion: fn() },
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    await userEvent.click(await within(menu).findByRole('menuitem', { name: 'Undo conversion...' }))
    await expect(args.onUndoConversion).toHaveBeenCalledOnce()
  },
}

export const UndoConversionHiddenWhenNothingRestorable: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    await expect(await within(menu).findByRole('menuitem', { name: /Page conversion/ })).toBeVisible()
    await expect(within(menu).queryByRole('menuitem', { name: /Undo conversion/ })).toBeNull()
  },
}

export const UndoConversionHiddenForNonAdmin: Story = {
  args: { onUndoConversion: fn(), canSetPageConversion: false },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    const menu = await body(canvasElement).findByRole('menu')
    await expect(await within(menu).findByRole('menuitem', { name: 'Rescan files' })).toBeVisible()
    await expect(within(menu).queryByRole('menuitem', { name: /Undo conversion/ })).toBeNull()
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

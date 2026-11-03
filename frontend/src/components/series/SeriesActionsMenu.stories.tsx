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
    onSetPageConversion: fn(),
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

type PlayContext = Parameters<NonNullable<Story['play']>>[0]

/** Opens the menu and walks the given rows, returning the page queries. */
async function openAt({ canvas, canvasElement, userEvent }: PlayContext, ...rows: (string | RegExp)[]) {
  await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
  const page = body(canvasElement)
  for (const name of rows) await userEvent.click(await page.findByRole('menuitem', { name }))
  return page
}

export const Admin: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx)
    for (const name of ['Files', 'Automation', 'Upgrades']) {
      await expect(await page.findByRole('menuitem', { name })).toBeVisible()
    }
    await expect(page.queryByRole('menuitem', { name: /^Monitor/ })).toBeNull()
    await ctx.userEvent.click(await page.findByRole('menuitem', { name: 'Refresh chapters' }))
    await waitFor(() => expect(ctx.args.onRefreshChapters).toHaveBeenCalledOnce())
  },
}

export const PickMonitorMode: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx, 'Automation')
    await expect(await page.findByText('Every chapter released from now on is wanted.')).toBeVisible()
    await ctx.userEvent.click(within(await page.findByRole('group', { name: 'Monitor' })).getByText('Smart'))
    await waitFor(() => expect(ctx.args.onSetMonitor).toHaveBeenCalledWith('Smart'))
    // A segmented choice applies in place: the page stays, with Notify and Incognito beside it.
    await expect(await page.findByRole('group', { name: 'Incognito' })).toBeVisible()
  },
}

/** Up and Down step from the back row to each control's choice; Left and Right walk a control. */
export const ArrowKeysThroughAutomation: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx, 'Automation')
    ;(await page.findByRole('menuitem', { name: /^Back/ })).focus()
    await ctx.userEvent.keyboard('{ArrowDown}')
    const monitor = await page.findByRole('group', { name: 'Monitor' })
    await waitFor(() => expect(within(monitor).getByRole('menuitemradio', { name: 'All' })).toHaveFocus())
    await ctx.userEvent.keyboard('{ArrowDown}')
    const notify = await page.findByRole('group', { name: 'Notify' })
    await waitFor(() => expect(within(notify).getByRole('menuitemradio', { name: 'Default' })).toHaveFocus())
    await ctx.userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(within(notify).getByRole('menuitemradio', { name: 'All' })).toHaveFocus())
    await ctx.userEvent.keyboard('{Enter}')
    await waitFor(() => expect(ctx.args.onSetNotify).toHaveBeenCalledWith('All'))
    // Left inside a control moves along it rather than leaving the page.
    await ctx.userEvent.keyboard('{ArrowLeft}')
    await expect(page.getByRole('group', { name: 'Monitor' })).toBeVisible()
  },
}

export const BackReturnsToGroupRow: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files')
    await expect(await page.findByRole('menuitem', { name: 'Rescan files' })).toBeVisible()
    ;(await page.findByRole('menuitem', { name: /^Back/ })).focus()
    await ctx.userEvent.keyboard('{Enter}')
    const files = await page.findByRole('menuitem', { name: 'Files' })
    await waitFor(() => expect(files).toHaveFocus())
    await expect(page.queryByRole('menuitem', { name: 'Rescan files' })).toBeNull()
  },
}

export const PointerNavigationKeepsArrowKeys: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files')
    const menu = await page.findByRole('menu')
    await waitFor(() => expect(menu).toHaveFocus())
    await ctx.userEvent.keyboard('{ArrowDown}')
    await waitFor(() => expect(menu.ownerDocument.activeElement?.getAttribute('role')).toBe('menuitem'))
  },
}

export const ArrowLeftGoesBack: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files', /Page conversion/)
    await expect(await page.findByText('Files already converted stay as they are.')).toBeVisible()
    await ctx.userEvent.keyboard('{ArrowLeft}')
    await expect(await page.findByRole('menuitem', { name: 'Rescan files' })).toBeVisible()
    await ctx.userEvent.keyboard('{ArrowLeft}')
    await expect(await page.findByRole('menuitem', { name: 'Edit metadata' })).toBeVisible()
  },
}

export const ReopensAtRoot: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx, 'Upgrades')
    await expect(await page.findByRole('menuitem', { name: 'Scan for upgrades' })).toBeVisible()
    await ctx.userEvent.keyboard('{Escape}')
    await waitFor(() => expect(page.queryByRole('menu')).toBeNull())
    await openAt(ctx)
    await expect(await page.findByRole('menuitem', { name: 'Edit metadata' })).toBeVisible()
  },
}

export const Remove: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx)
    await ctx.userEvent.click(await page.findByRole('menuitem', { name: 'Remove from library' }))
    await waitFor(() => expect(ctx.args.onRemove).toHaveBeenCalledOnce())
  },
}

export const ReaderPermissions: Story = {
  args: { canEdit: false, canCleanUpReadFiles: false, canSetPageConversion: false, canScanUpgrades: false, canArchive: false, canRemove: false },
  play: async (ctx) => {
    const page = await openAt(ctx)
    await expect(await page.findByRole('group', { name: 'Notify' })).toBeVisible()
    await expect(page.queryByRole('menuitem', { name: 'Edit metadata' })).toBeNull()
    await expect(page.queryByRole('menuitem', { name: 'Files' })).toBeNull()
    await expect(page.queryByRole('menuitem', { name: 'Automation' })).toBeNull()
    await expect(page.queryByRole('menuitem', { name: 'Upgrades' })).toBeNull()
    await expect(page.queryByRole('menuitem', { name: /remove/i })).toBeNull()
  },
}

export const ReaderPicksNotify: Story = {
  args: ReaderPermissions.args,
  play: async (ctx) => {
    const page = await openAt(ctx)
    await ctx.userEvent.click(within(await page.findByRole('group', { name: 'Notify' })).getByText('Muted'))
    await waitFor(() => expect(ctx.args.onSetNotify).toHaveBeenCalledWith('Muted'))
  },
}

export const PickPageConversion: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files')
    const item = await page.findByRole('menuitem', { name: /Page conversion/ })
    await expect(item).toHaveTextContent('Rules')
    await ctx.userEvent.click(item)
    await waitFor(() => expect(page.getByRole('menuitemradio', { name: 'Follow the rules' })).toHaveAttribute('aria-checked', 'true'))
    for (const name of ['Never', 'Smallest', 'Compact', 'Balanced', 'High', 'Lossless']) {
      await expect(await page.findByRole('menuitemradio', { name })).toBeVisible()
    }
    const balanced = page.getByRole('menuitemradio', { name: 'Balanced' })
    balanced.focus()
    await ctx.userEvent.keyboard('{Enter}')
    await waitFor(() => expect(ctx.args.onSetPageConversion).toHaveBeenCalledWith('Balanced'))
  },
}

export const PageConversionNever: Story = {
  args: { pageConversion: 'Never' },
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files')
    const item = await page.findByRole('menuitem', { name: /Page conversion/ })
    await expect(item).toHaveTextContent('Never')
    await ctx.userEvent.click(item)
    await waitFor(() => expect(page.getByRole('menuitemradio', { name: 'Never' })).toHaveAttribute('aria-checked', 'true'))
    await expect(ctx.args.onSetPageConversion).not.toHaveBeenCalled()
  },
}

export const PageConversionFixedStop: Story = {
  args: { pageConversion: 'Lossless' },
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files')
    await expect(await page.findByRole('menuitem', { name: /Page conversion/ })).toHaveTextContent('Lossless')
  },
}

export const UndoConversion: Story = {
  args: { onUndoConversion: fn() },
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files')
    await ctx.userEvent.click(await page.findByRole('menuitem', { name: 'Undo conversion...' }))
    await waitFor(() => expect(ctx.args.onUndoConversion).toHaveBeenCalledOnce())
  },
}

export const UndoConversionHiddenWhenNothingRestorable: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files')
    await expect(await page.findByRole('menuitem', { name: /Page conversion/ })).toBeVisible()
    await expect(page.queryByRole('menuitem', { name: /Undo conversion/ })).toBeNull()
  },
}

export const UndoConversionHiddenForNonAdmin: Story = {
  args: { onUndoConversion: fn(), canSetPageConversion: false },
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files')
    await expect(await page.findByRole('menuitem', { name: 'Rescan files' })).toBeVisible()
    await expect(page.queryByRole('menuitem', { name: /Undo conversion/ })).toBeNull()
  },
}

export const ScanRunning: Story = {
  args: { scanningUpgrades: true, searchingVolumes: true },
  play: async (ctx) => {
    const page = await openAt(ctx, 'Upgrades')
    await waitFor(() => expect(page.getByRole('menuitem', { name: 'Scan for upgrades' })).toBeDisabled())
  },
}

export const ProfilePinnedAndCleanupOn: Story = {
  args: { upgradeProfileId: 2, readFileCleanup: 'On', readFileCleanupActive: true, incognito: 'ScrobbleOnly', notificationMode: 'Muted', monitorMode: 'MainOnly' },
  play: async (ctx) => {
    const page = await openAt(ctx, 'Automation')
    const notify = await page.findByRole('group', { name: 'Notify' })
    await waitFor(() => expect(within(notify).getByRole('menuitemradio', { name: 'Muted' })).toHaveAttribute('aria-checked', 'true'))
    const incognito = await page.findByRole('group', { name: 'Incognito' })
    await waitFor(() =>
      expect(within(incognito).getByRole('menuitemradio', { name: 'No scrobble' })).toHaveAttribute('aria-checked', 'true'),
    )
    await expect(page.getByText("Reading counts in your stats but isn't sent to your trackers.")).toBeVisible()
  },
}

export const ArchivedSourceToolsOff: Story = {
  args: { archived: true, sourceToolsDisabledReason: 'Restore the series to use its sources' },
  play: async (ctx) => {
    const page = await openAt(ctx)
    await expect(await page.findByRole('menuitem', { name: 'Restore to library' })).toBeVisible()
    // The reason is written out, not left to a tooltip.
    await expect(await page.findByText('Restore the series to use its sources')).toBeVisible()
    await ctx.userEvent.click(await page.findByRole('menuitem', { name: 'Automation' }))
    const monitor = await page.findByRole('group', { name: 'Monitor' })
    await waitFor(() => expect(within(monitor).getByRole('menuitemradio', { name: 'Smart' })).toBeDisabled())
    await expect(page.getByText('Restore the series to use its sources')).toBeVisible()
  },
}

export const OfflineSaving: Story = {
  args: { offlineSaved: 12, onSaveUnreadOffline: fn(), onRemoveOffline: fn() },
  play: async (ctx) => {
    const page = await openAt(ctx, 'This device')
    await ctx.userEvent.click(await page.findByRole('menuitem', { name: 'Save unread chapters for offline' }))
    await waitFor(() => expect(ctx.args.onSaveUnreadOffline).toHaveBeenCalledOnce())
  },
}

/** A running refresh holds its own item only; the menu and the other actions stay usable. */
export const RefreshRunning: Story = {
  args: { refreshingChapters: true },
  play: async (ctx) => {
    const page = await openAt(ctx)
    await waitFor(() => expect(page.getByRole('menuitem', { name: 'Refresh chapters' })).toBeDisabled())
    await ctx.userEvent.click(await page.findByRole('menuitem', { name: 'Edit metadata' }))
    await waitFor(() => expect(ctx.args.onEditMetadata).toHaveBeenCalledOnce())
  },
}

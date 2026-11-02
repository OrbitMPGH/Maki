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
    const page = await openAt(ctx, 'Automation', /^Monitor/)
    await expect(await page.findByText('What happens to chapters released later')).toBeVisible()
    const smart = await page.findByRole('menuitemradio', { name: 'Smart' })
    smart.focus()
    await ctx.userEvent.keyboard('{Enter}')
    await waitFor(() => expect(ctx.args.onSetMonitor).toHaveBeenCalledWith('Smart'))
    await expect(await page.findByRole('menuitem', { name: /^Incognito/ })).toBeVisible()
  },
}

export const BackReturnsToGroupRow: Story = {
  play: async (ctx) => {
    const page = await openAt(ctx, 'Files')
    await expect(await page.findByRole('menuitem', { name: 'Rescan files' })).toBeVisible()
    ;(await page.findByRole('menuitem', { name: 'Back' })).focus()
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
    const page = await openAt(ctx, 'Automation', /^Incognito/)
    await expect(await page.findByText(/Full also excludes this series/)).toBeVisible()
    await ctx.userEvent.keyboard('{ArrowLeft}')
    await expect(await page.findByRole('menuitem', { name: /^Notify/ })).toBeVisible()
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
    await expect(await page.findByRole('menuitem', { name: /^Notify/ })).toBeVisible()
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
    const page = await openAt(ctx, /^Notify/)
    const muted = await page.findByRole('menuitemradio', { name: 'Muted' })
    muted.focus()
    await ctx.userEvent.keyboard('{Enter}')
    await waitFor(() => expect(ctx.args.onSetNotify).toHaveBeenCalledWith('Muted'))
    await expect(await page.findByRole('menuitem', { name: /^Notify/ })).toBeVisible()
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
    await expect(await page.findByRole('menuitem', { name: /^Notify/ })).toHaveTextContent('Muted')
    await expect(await page.findByRole('menuitem', { name: /^Incognito/ })).toHaveTextContent('No scrobble')
  },
}

export const ArchivedSourceToolsOff: Story = {
  args: { archived: true, sourceToolsDisabledReason: 'Restore the series to use its sources' },
  play: async (ctx) => {
    const page = await openAt(ctx)
    await expect(await page.findByRole('menuitem', { name: 'Restore to library' })).toBeVisible()
    await ctx.userEvent.click(await page.findByRole('menuitem', { name: 'Automation' }))
    const monitor = await page.findByRole('menuitem', { name: /^Monitor/ })
    await expect(monitor).toHaveAttribute('aria-disabled', 'true')
    await ctx.userEvent.click(monitor)
    await expect(page.queryByRole('menuitemradio', { name: 'Smart' })).toBeNull()
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

export const Busy: Story = {
  args: { busy: true },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: 'More actions' })).toBeDisabled())
  },
}

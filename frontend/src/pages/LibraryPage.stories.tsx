import { useEffect } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent as userEventApi, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { archivedLibraryHandlers, libraryHandlers, seedLibraryPrefs } from '../../.storybook/mocks/library'
import { getConnectivity, onConnectivityChange, setConnectivity } from '../lib/connectivity'
import LibraryPage from './LibraryPage'

const meta = {
  component: LibraryPage,
  tags: ['ai-generated'],
  parameters: {
    layout: 'fullscreen',
    router: { initialEntries: ['/library'] },
    // Home answers /series and /rootfolder first in the shared defaults, so the library set rides along here.
    msw: libraryHandlers,
  },
  beforeEach: () => seedLibraryPrefs(),
} satisfies Meta<typeof LibraryPage>

export default meta
type Story = StoryObj<typeof meta>

type Canvas = ReturnType<typeof within>
type UserEvent = Pick<typeof userEventApi, 'click'>

const body = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body)

async function enterSelectMode(canvas: Canvas, userEvent: UserEvent, waitFor = /Lanterns Over Kasumi/) {
  await canvas.findByRole('link', { name: waitFor })
  await userEvent.click(await canvas.findByRole('button', { name: /^select$/i }))
}

async function pick(canvas: Canvas, userEvent: UserEvent, ...titles: string[]) {
  for (const title of titles) {
    // Cover cards are checkboxes while selecting; list rows stay links.
    const name = new RegExp(title)
    await userEvent.click(canvas.queryByRole('checkbox', { name }) ?? (await canvas.findByRole('link', { name })))
  }
}

export const Grid: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })).toBeVisible()
    await expect(await canvas.findByText('14 series')).toBeVisible()
  },
}

/** The Archived view: three archived cards, one with files removed and one with chapters read past. */
export const ArchivedView: Story = {
  args: { scope: 'archived' },
  parameters: { router: { path: '/library/archived', initialEntries: ['/library/archived'] }, msw: archivedLibraryHandlers },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: /The Ninth Kitchen/ })).toBeVisible()
    await expect(await canvas.findByRole('link', { name: /Paper Moon Detective/ })).toBeVisible()
    await expect(canvas.queryByRole('link', { name: /Lanterns Over Kasumi/ })).toBeNull()
    await waitFor(() => expect(canvas.getAllByText(/^Archived \w+ \d+, \d{4}$/)).toHaveLength(3))
    await expect(await canvas.findByText('Files removed', { selector: '.mantine-Badge-label' })).toBeVisible()
    await expect(await canvas.findByText('2 skipped')).toBeVisible()
  },
}

export const ArchivedViewList: Story = {
  args: { scope: 'archived' },
  parameters: { router: { path: '/library/archived', initialEntries: ['/library/archived'] }, msw: archivedLibraryHandlers },
  beforeEach: () => seedLibraryPrefs({ view: 'list' }),
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(canvasElement.querySelectorAll('.series-row')).toHaveLength(3))
  },
}

export const ArchivedRestoreAsksAboutFiles: Story = {
  args: { scope: 'archived' },
  parameters: { router: { path: '/library/archived', initialEntries: ['/library/archived'] }, msw: archivedLibraryHandlers },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('link', { name: /Under the Willow Gate/ })
    await userEvent.click(await canvas.findByRole('button', { name: 'Actions for Under the Willow Gate' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Restore to library' }))
    const dialog = await body(canvasElement).findByRole('dialog')
    await expect(await within(dialog).findByText('Download the 75 removed chapters again?')).toBeVisible()
  },
}

export const NothingArchived: Story = {
  args: { scope: 'archived' },
  parameters: { router: { path: '/library/archived', initialEntries: ['/library/archived'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing archived')).toBeVisible()
  },
}

// Every API response flips connectivity back to online, so the offline story pins it while mounted.
function ForceOffline({ children }: { children: React.ReactNode }) {
  useEffect(() => {
    const pin = () => {
      if (getConnectivity() === 'online') setConnectivity('offline')
    }
    pin()
    const stop = onConnectivityChange(pin)
    return () => {
      stop()
      setConnectivity('online')
    }
  }, [])
  return <>{children}</>
}

/** Offline with nothing on this device: every card is dimmed and says it is not available. */
export const OfflineNothingSaved: Story = {
  decorators: [(Story) => <ForceOffline><Story /></ForceOffline>],
  play: async ({ canvas }) => {
    await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })
    await waitFor(() => expect(canvas.getAllByText('Not available offline').length).toBeGreaterThan(5))
  },
}

export const ListView: Story = {
  beforeEach: () => seedLibraryPrefs({ view: 'list' }),
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(canvasElement.querySelectorAll('.series-row')).toHaveLength(14))
  },
}

export const DensityChoiceIsKept: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })
    await userEvent.click(await canvas.findByRole('button', { name: 'More options' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Compact' }))
    await waitFor(() => expect(localStorage.getItem('library-density')).toBe('compact'))

    await userEvent.click(await canvas.findByRole('button', { name: 'More options' }))
    const compact = await body(canvasElement).findByRole('menuitem', { name: 'Compact' })
    await waitFor(() => expect(compact).toHaveAttribute('aria-current', 'true'))
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/series'), () => neverResolve()), ...libraryHandlers] },
}

export const LoadError: Story = {
  parameters: { msw: [http.get(api('/series'), () => failWith(500, 'The database is locked')), ...libraryHandlers] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Failed to load library')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

export const EmptyLibrary: Story = {
  parameters: { msw: [http.get(api('/series'), () => HttpResponse.json([])), ...libraryHandlers] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Your library is empty')).toBeVisible()
    await expect(await canvas.findByRole('link', { name: /Import a folder/ })).toBeVisible()
  },
}

export const NoReadTracking: Story = {
  parameters: { msw: [http.get(api('/reader/used'), () => HttpResponse.json({ used: false })), ...libraryHandlers] },
}

export const ReaderView: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })
    await expect(await canvas.findByRole('link', { name: 'Request series' })).toBeVisible()
    await expect(canvas.queryByRole('link', { name: 'Add series' })).not.toBeInTheDocument()
  },
}

export const PhoneLayout: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Filters' })).toBeVisible()
  },
}

export const SearchMatches: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { query: 'kasumi' } }),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('1 of 14 series match')).toBeVisible()
    await expect(canvas.queryByRole('link', { name: /The Ninth Kitchen/ })).not.toBeInTheDocument()
  },
}

export const NoMatches: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { query: 'zzzz nothing like this' } }),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No matches')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Clear all filters' })).toBeVisible()
  },
}

export const OngoingAndBehind: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { status: 'Ongoing', completeness: 'behind', sort: 'incomplete' } }),
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByText('5 of 14 series match')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Save filter' })).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear' }))
    await expect(await canvas.findByText('14 series')).toBeVisible()
  },
}

export const SortedByTitleReversed: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { sort: 'title', 'sort-dir': 'desc' } }),
  play: async ({ canvasElement }) => {
    await waitFor(() => expect(canvasElement.querySelector('.cover-title')).toHaveTextContent('untitled_import_0042'))
  },
}

export const ReverseSortOrder: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    // Newest first by default: the freshly imported series leads.
    await waitFor(() => expect(canvasElement.querySelector('.cover-title')).toHaveTextContent('untitled_import_0042'))
    await userEvent.click(await canvas.findByRole('button', { name: 'Reverse sort order' }))
    await waitFor(() => expect(canvasElement.querySelector('.cover-title')).toHaveTextContent('Under the Willow Gate'))
  },
}

export const OverflowMenu: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })
    await userEvent.click(await canvas.findByRole('button', { name: 'More options' }))
    const menu = body(canvasElement)
    await expect(await menu.findByRole('menuitem', { name: 'Manage tags' })).toBeInTheDocument()
    await expect(await menu.findByRole('menuitem', { name: 'Comfortable' })).toBeInTheDocument()
  },
}

export const ManageTagsFromMenu: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })
    await userEvent.click(await canvas.findByRole('button', { name: 'More options' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Manage tags' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Manage tags' })
    await expect(await within(dialog).findByText('Favourites')).toBeVisible()
  },
}

export const FilterDrawer: Story = {
  beforeEach: () =>
    seedLibraryPrefs({ page: { 'filters-open': true, 'filter-groups': ['series', 'tags', 'ratings', 'monitoring'] } }),
  play: async ({ canvasElement }) => {
    const drawer = await body(canvasElement).findByRole('dialog', { name: 'Filters' })
    await expect(await within(drawer).findByText(/14 of 14 series shown/)).toBeVisible()
    await expect(await within(drawer).findByRole('textbox', { name: 'Minimum chapters' })).toBeVisible()
    await expect(await within(drawer).findByRole('switch', { name: /Untagged only/ })).toBeInTheDocument()
  },
}

export const FilterDrawerWithActiveFilters: Story = {
  beforeEach: () =>
    seedLibraryPrefs({
      page: {
        'filters-open': true,
        'filter-groups': ['series', 'tags'],
        status: 'Ongoing',
        types: ['manhwa'],
        tags: ['4'],
        genres: ['Fantasy'],
      },
    }),
  play: async ({ canvasElement }) => {
    const drawer = await body(canvasElement).findByRole('dialog', { name: 'Filters' })
    await expect(await within(drawer).findByText(/1 of 14 series shown/)).toBeVisible()
    await expect(await within(drawer).findByRole('button', { name: 'Clear all filters' })).toBeVisible()
  },
}

export const FilterDrawerClearAll: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { 'filters-open': true, status: 'Completed', monitored: 'unmonitored' } }),
  play: async ({ canvasElement, userEvent }) => {
    const drawer = await body(canvasElement).findByRole('dialog', { name: 'Filters' })
    await expect(await within(drawer).findByText(/^[0-9] of 14 series shown/)).toBeVisible()
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Clear all filters' }))
    await expect(await within(drawer).findByText(/14 of 14 series shown/)).toBeVisible()
  },
}

export const SavedFilterChips: Story = {
  play: async ({ canvas, userEvent }) => {
    const chip = await canvas.findByRole('button', { name: 'Ongoing and behind' })
    await userEvent.click(chip)
    await expect(await canvas.findByText('5 of 14 series match')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Clear' })).toBeVisible()
  },
}

const onSaveFilter = fn()

export const SaveFilterDialog: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { status: 'Hiatus' } }),
  parameters: {
    msw: [
      http.post(api('/library/filters'), async ({ request }) => {
        const payload = (await request.json()) as { name: string }
        onSaveFilter(payload)
        return HttpResponse.json({ id: 99, name: payload.name, spec: {}, sortOrder: 3 })
      }),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Save filter' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Save this filter' })
    await userEvent.type(await within(dialog).findByLabelText('Name'), 'On hiatus')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Save' }))
    await waitFor(() => expect(onSaveFilter).toHaveBeenCalledWith(expect.objectContaining({ name: 'On hiatus' })))
  },
}

export const DeleteSavedFilter: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const buttons = await canvas.findAllByRole('button', { name: 'Delete saved filter' })
    await userEvent.click(buttons[0])
    await expect(await body(canvasElement).findByText(/Deleted saved filter Ongoing and behind/)).toBeVisible()
    await expect(await body(canvasElement).findByRole('button', { name: 'Undo' })).toBeVisible()
  },
}

export const NoSavedFilters: Story = {
  parameters: { msw: [http.get(api('/library/filters'), () => HttpResponse.json([])), ...libraryHandlers] },
  play: async ({ canvas }) => {
    await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })
    await expect(canvas.queryByText('Saved filters')).not.toBeInTheDocument()
  },
}

export const SelectMode: Story = {
  play: async ({ canvas, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await expect(await canvas.findByText('0 selected')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Tags' })).toBeDisabled())
    await pick(canvas, userEvent, 'Lanterns Over Kasumi', 'Snowfield Letters')
    await expect(await canvas.findByText('2 selected')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Tags' })).toBeEnabled())
    await userEvent.click(await canvas.findByRole('button', { name: 'Select all' }))
    await expect(await canvas.findByText('14 selected')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear all' }))
    await expect(await canvas.findByText('0 selected')).toBeVisible()
  },
}

export const SelectModeList: Story = {
  beforeEach: () => seedLibraryPrefs({ view: 'list' }),
  play: async ({ canvas, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Paper Moon Detective')
    await expect(await canvas.findByText('1 selected')).toBeVisible()
  },
}

export const SelectFiltered: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { status: 'Completed' } }),
  play: async ({ canvas, userEvent }) => {
    await enterSelectMode(canvas, userEvent, /The Ninth Kitchen/)
    await userEvent.click(await canvas.findByRole('button', { name: 'Select filtered' }))
    await expect(await canvas.findByText('4 selected')).toBeVisible()
  },
}

export const ReaderSelectMode: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Lanterns Over Kasumi')
    // A reader holds none of the library permissions, so no Tags, Monitoring or Remove buttons.
    await expect(canvas.queryByRole('button', { name: 'Tags' })).not.toBeInTheDocument()
    await expect(canvas.queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Notifications' })).toBeEnabled())
  },
}

const onBulkTag = fn()

export const BulkTagDialog: Story = {
  parameters: {
    msw: [
      http.post(api('/tags/bulk'), async ({ request }) => {
        onBulkTag(await request.json())
        return HttpResponse.json({ updated: 2 })
      }),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Lanterns Over Kasumi', 'The Ninth Kitchen')
    await userEvent.click(await canvas.findByRole('button', { name: 'Tags' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Tag 2 series' })
    await userEvent.click(await within(dialog).findByLabelText('Add'))
    await userEvent.click(await body(canvasElement).findByRole('option', { name: /^To re-read/ }))
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Apply' }))
    await waitFor(() => expect(onBulkTag).toHaveBeenCalledWith({ seriesIds: [1, 2], add: [2], remove: [] }))
    await expect(await body(canvasElement).findByText('Tagged 2 series')).toBeVisible()
  },
}

export const BulkRemoveDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Bluefin Boys')
    await userEvent.click(await canvas.findByRole('button', { name: 'Remove' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Remove 1 series?' })
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: 'Also delete files on disk' }))
    await expect(await within(dialog).findByText(/recycle bin for 14 days/)).toBeVisible()
  },
}

export const BulkRemoveRuns: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Bluefin Boys', 'Under the Willow Gate')
    await userEvent.click(await canvas.findByRole('button', { name: 'Remove' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Remove 2 series?' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Remove' }))
    await expect(await body(canvasElement).findByText(/Remove: 2\/2 succeeded/)).toBeVisible()
  },
}

export const BulkMonitoringDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Clockwork Tea House')
    await userEvent.click(await canvas.findByRole('button', { name: 'Monitoring' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Set monitoring for 1 series' })
    await userEvent.click(await within(dialog).findByText('Smart'))
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Apply' }))
    await expect(await body(canvasElement).findByText(/Set monitoring: 1\/1 succeeded/)).toBeVisible()
  },
}

export const BulkSearchMissing: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Iron Petal Academy', 'Ashen Crown Regression')
    await userEvent.click(await canvas.findByRole('button', { name: 'Search missing' }))
    await expect(await body(canvasElement).findByText(/Search missing: 2\/2 succeeded/)).toBeVisible()
  },
}

export const BulkSearchMissingPartlyFails: Story = {
  parameters: {
    msw: [
      http.post(api('/series/:id/searchmissing'), ({ params }) =>
        params.id === '3' ? failWith(409, 'A download for this series is already running') : HttpResponse.json({ queued: 2 }),
      ),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Iron Petal Academy', 'Ashen Crown Regression')
    await userEvent.click(await canvas.findByRole('button', { name: 'Search missing' }))
    await expect(await body(canvasElement).findByText(/Search missing: 1\/2 succeeded, first error/)).toBeVisible()
  },
}

async function openMoreAction(canvas: Canvas, canvasElement: HTMLElement, userEvent: UserEvent, item: string) {
  await userEvent.click(await canvas.findByRole('button', { name: /^more$/i }))
  await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: item }))
}

export const BulkAutoMatchDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Bluefin Boys', 'Grave Robber of the Seventh Hall', 'Neon Shrine Maiden')
    await openMoreAction(canvas, canvasElement, userEvent, 'Auto-match')
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Auto-match sources for 3 series' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Auto-match' }))
    await expect(await body(canvasElement).findByText('Auto-matching 3 series in the background.')).toBeVisible()
  },
}

export const BulkQualityProfileDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Snowfield Letters')
    await openMoreAction(canvas, canvasElement, userEvent, 'Quality profile')
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Set quality profile for 1 series' })
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Quality profile' }))
    await userEvent.click(await body(canvasElement).findByRole('option', { name: 'Official first' }))
    await expect(await within(dialog).findByText(/Prefers official and volume releases/)).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Apply' }))
    await expect(await body(canvasElement).findByText('Quality profile set for 1 series')).toBeVisible()
  },
}

export const BulkNotificationsDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Snowfield Letters', 'Summer at Shiokaze Station')
    await openMoreAction(canvas, canvasElement, userEvent, 'Notifications')
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Set notifications for 2 series' })
    await expect(await within(dialog).findByRole('radiogroup', { name: 'Notifications' })).toBeVisible()
  },
}

export const BulkMoveDialog: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await pick(canvas, userEvent, 'Iron Petal Academy')
    await openMoreAction(canvas, canvasElement, userEvent, 'Move')
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Move 1 series' })
    const move = await within(dialog).findByRole('button', { name: 'Move' })
    await waitFor(() => expect(move).toBeDisabled())
    await userEvent.click(await within(dialog).findByRole('combobox', { name: 'Destination root folder' }))
    await userEvent.click(await body(canvasElement).findByRole('option', { name: '/library/manhwa' }))
    await waitFor(() => expect(move).toBeEnabled())
  },
}

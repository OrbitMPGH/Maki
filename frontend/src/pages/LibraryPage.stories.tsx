import { useEffect } from 'react'
import { useLocation } from 'react-router-dom'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent as userEventApi, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { archivedLibraryHandlers, bulkJobHandlers, libraryHandlers, savedFilters, seedLibraryPrefs } from '../../.storybook/mocks/library'
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
    // Unfiltered, the figure strip carries the total and the toolbar shows no count.
    expect(canvas.queryByText(/ of 14$/)).toBeNull()
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
    await waitFor(() => expect(canvasElement.querySelectorAll('.archived-row')).toHaveLength(3))
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

export const TableView: Story = {
  beforeEach: () => seedLibraryPrefs({ view: 'table' }),
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('table', { name: 'Library' })
    await waitFor(() => expect(canvasElement.querySelectorAll('.library-table-body > [role="row"]')).toHaveLength(14))
    await waitFor(() => expect(canvas.getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Title', 'Status', 'Chapters', 'Unread', 'Last read', 'Latest release', 'Size on disk', 'Sources',
    ]))
  },
}

/** A header with a Library sort sets it; clicking it again reverses it. Sources has no sort and is not a button. */
export const TableHeaderSorts: Story = {
  beforeEach: () => seedLibraryPrefs({ view: 'table' }),
  play: async ({ canvas, userEvent }) => {
    await canvas.findByRole('table', { name: 'Library' })
    await userEvent.click(await canvas.findByRole('button', { name: 'Title' }))
    const title = () => canvas.getAllByRole('columnheader').find((h) => h.textContent === 'Title')
    await waitFor(() => expect(title()).toHaveAttribute('aria-sort', 'ascending'))
    await userEvent.click(await canvas.findByRole('button', { name: 'Title' }))
    await waitFor(() => expect(title()).toHaveAttribute('aria-sort', 'descending'))
    expect(canvas.queryByRole('button', { name: 'Sources' })).toBeNull()
  },
}

export const TableColumnPicker: Story = {
  beforeEach: () => seedLibraryPrefs({ view: 'table' }),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('table', { name: 'Library' })
    await userEvent.click(await canvas.findByRole('button', { name: 'Columns' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Columns' })
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: 'Tags' }))
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: 'Size on disk' }))
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Move Tags up' }))
    await waitFor(() =>
      expect(JSON.parse(localStorage.getItem('library-table-columns') ?? '[]')).toEqual([
        'status', 'chapters', 'unread', 'lastRead', 'released', 'tags', 'sources',
      ]),
    )
    await waitFor(() => expect(canvas.getAllByRole('columnheader').at(-2)?.textContent).toBe('Tags'))
  },
}

export const TableColumnResize: Story = {
  beforeEach: () => seedLibraryPrefs({ view: 'table' }),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('table', { name: 'Library' })
    const box = (col: string) => canvasElement.querySelector(`.library-table-th[data-col="${col}"]`)!.getBoundingClientRect()
    const rowBox = (col: string) =>
      canvasElement.querySelector(`.library-table-body > [role="row"] [data-col="${col}"]`)!.getBoundingClientRect()
    const stored = () => JSON.parse(localStorage.getItem('library-table-widths') ?? '{}')
    const titleWidth = box('title').width
    const statusLeft = box('status').left

    // Shrinking Title pins it and pulls every column after it left, rows included.
    ;(await canvas.findByRole('separator', { name: 'Resize Title column' })).focus()
    await userEvent.keyboard('{ArrowLeft}')
    await waitFor(() => expect(stored().title).toBeLessThan(Math.round(titleWidth)))
    const shrunk = Math.round(titleWidth) - stored().title
    await waitFor(() => expect(box('status').left).toBeCloseTo(statusLeft - shrunk, 0))
    await waitFor(() => expect(rowBox('status').left).toBeCloseTo(statusLeft - shrunk, 0))

    // Widening Status pushes the next column right and leaves Status where it is.
    const chaptersLeft = box('chapters').left
    const status = await canvas.findByRole('separator', { name: 'Resize Status column' })
    status.focus()
    await userEvent.keyboard('{ArrowRight}{ArrowRight}')
    await waitFor(() => expect(stored().status).toBe(160))
    await waitFor(() => expect(box('chapters').left).toBeCloseTo(chaptersLeft + 32, 0))
    await waitFor(() => expect(box('status').left).toBeCloseTo(statusLeft - shrunk, 0))

    // Double-click resets: Status to its default, Title back to filling the row.
    await userEvent.dblClick(status)
    await userEvent.dblClick(await canvas.findByRole('separator', { name: 'Resize Title column' }))
    await waitFor(() => expect(localStorage.getItem('library-table-widths')).toBeNull())
    await waitFor(() => expect(box('status').left).toBeCloseTo(statusLeft, 0))
  },
}

export const TableSelectRangeWithShift: Story = {
  beforeEach: () => seedLibraryPrefs({ view: 'table' }),
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('table', { name: 'Library' })
    await userEvent.click(await canvas.findByRole('button', { name: /^select$/i }))
    const rows = () => [...canvasElement.querySelectorAll<HTMLElement>('.library-table-body > [role="row"]')]
    await waitFor(() => expect(rows().length).toBe(14))
    await userEvent.click(rows()[1].querySelector('[data-col="status"]')!)
    await expect(await canvas.findByText('1 selected')).toBeVisible()
    await userEvent.keyboard('{Shift>}')
    await userEvent.click(rows()[4].querySelector('[data-col="status"]')!)
    await userEvent.keyboard('{/Shift}')
    await expect(await canvas.findByText('4 selected')).toBeVisible()
    await waitFor(() => expect(rows()[0]).not.toHaveAttribute('data-selected'))
  },
}

export const TableViewFromTheLink: Story = {
  decorators: [
    (Story) => (
      <>
        <LocationProbe />
        <Story />
      </>
    ),
  ],
  parameters: { router: { initialEntries: ['/library?view=table'] } },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('table', { name: 'Library' })
    // The link does not change what this browser remembers.
    expect(localStorage.getItem('library-view')).toBeNull()
    await userEvent.click(await canvas.findByRole('radio', { name: 'List view' }))
    const probe = () => canvasElement.ownerDocument.querySelector('[data-testid="location"]')
    await waitFor(() => expect(probe()?.textContent).toBe('?view=list'))
  },
}

/** Grouped by status from a link: one header per status, and a fold is remembered by the browser. */
export const GroupedByStatus: Story = {
  parameters: { router: { initialEntries: ['/library?groupBy=status'] } },
  play: async ({ canvasElement, userEvent }) => {
    await waitFor(() => expect(canvasElement.querySelector('.library-groups')).not.toBeNull())
    const groups = within(canvasElement.querySelector<HTMLElement>('.library-groups')!)
    const ongoing = await groups.findByRole('button', { name: /^Ongoing/ })
    await waitFor(() => expect(ongoing).toHaveAttribute('aria-expanded', 'true'))
    const cards = () => canvasElement.querySelectorAll('.cover-card').length
    await waitFor(() => expect(cards()).toBe(14))
    await userEvent.click(ongoing)
    await waitFor(() => expect(ongoing).toHaveAttribute('aria-expanded', 'false'))
    await waitFor(() => expect(cards()).toBeLessThan(14))
    await waitFor(() => expect(JSON.parse(localStorage.getItem('library-collapsed-groups') ?? '[]')).toEqual(['status:Ongoing']))
  },
}

/** By tag, a series with two tags is listed under both, and untagged series close the list. */
export const GroupedByTagInTable: Story = {
  beforeEach: () => seedLibraryPrefs({ view: 'table', page: { 'group-by': 'tag' } }),
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('table', { name: 'Library' })
    const names = () => [...canvasElement.querySelectorAll('.library-group-name')].map((n) => n.textContent)
    await waitFor(() =>
      expect(names()).toEqual(['Comfort reads', 'Favourites', 'Shared with Sam', 'To re-read', 'Waiting for volume', 'No tag']),
    )
    await waitFor(() => expect(canvas.getAllByRole('link', { name: /Lanterns Over Kasumi/ })).toHaveLength(2))
  },
}

export const GroupByWritesTheLink: Story = {
  decorators: [
    (Story) => (
      <>
        <LocationProbe />
        <Story />
      </>
    ),
  ],
  play: async ({ canvas, canvasElement, userEvent }) => {
    await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })
    await userEvent.click(await canvas.findByRole('combobox', { name: 'Group by' }))
    await userEvent.click(await body(canvasElement).findByRole('option', { name: 'Type' }))
    const probe = () => canvasElement.ownerDocument.querySelector('[data-testid="location"]')
    await waitFor(() => expect(probe()?.textContent).toContain('groupBy=type'))
    await expect(await canvas.findByRole('button', { name: /^Manga/ })).toBeVisible()
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
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByRole('button', { name: 'Filters' })).toBeVisible()
    // Two posters a row at Default density: the third card starts a new row.
    await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })
    const cards = [...canvasElement.querySelectorAll('.cover-card')].slice(0, 3)
    const tops = cards.map((c) => Math.round(c.getBoundingClientRect().top))
    await expect(tops[0]).toBe(tops[1])
    await expect(tops[2]).toBeGreaterThan(tops[0])
  },
}

export const SearchMatches: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { query: 'kasumi' } }),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('1 of 14')).toBeVisible()
    await expect(canvas.queryByRole('link', { name: /The Ninth Kitchen/ })).not.toBeInTheDocument()
  },
}

export const SearchToleratesTypos: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { query: 'lantrens over kasmui' } }),
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('1 of 14')).toBeVisible()
    await expect(await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })).toBeVisible()
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
    await expect(await canvas.findByText('5 of 14')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Save filter' })).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear' }))
    await waitFor(() => expect(canvas.queryByText('5 of 14')).toBeNull())
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
  beforeEach: () => seedLibraryPrefs({ page: { 'filters-open': true } }),
  play: async ({ canvasElement, userEvent }) => {
    const drawer = await body(canvasElement).findByRole('dialog', { name: 'Filters' })
    await expect(await within(drawer).findByText('14 series')).toBeVisible()
    await userEvent.click(await within(drawer).findByRole('button', { name: /^Chapters/ }))
    await expect(await within(drawer).findByRole('textbox', { name: 'Minimum chapters' })).toBeVisible()
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Back Chapters' }))
    await userEvent.click(await within(drawer).findByRole('button', { name: /^Your tags/ }))
    await expect(await within(drawer).findByRole('switch', { name: /Untagged only/ })).toBeInTheDocument()
  },
}

export const FilterDrawerWithActiveFilters: Story = {
  beforeEach: () =>
    seedLibraryPrefs({
      page: {
        'filters-open': true,
        status: 'Ongoing',
        types: ['manhwa'],
        tags: ['4'],
        genres: ['Fantasy'],
      },
    }),
  play: async ({ canvasElement }) => {
    const drawer = await body(canvasElement).findByRole('dialog', { name: 'Filters' })
    await expect(await within(drawer).findByText('1 series')).toBeVisible()
    await waitFor(() => expect(within(drawer).getByRole('button', { name: 'Reset' })).toBeEnabled())
    await waitFor(() => expect(within(drawer).getByRole('button', { name: 'Ongoing', pressed: true })).toBeVisible())
  },
}

export const FilterDrawerClearAll: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { 'filters-open': true, status: 'Completed', monitored: 'unmonitored' } }),
  play: async ({ canvasElement, userEvent }) => {
    const drawer = await body(canvasElement).findByRole('dialog', { name: 'Filters' })
    await expect(await within(drawer).findByText(/^[0-9] series$/)).toBeVisible()
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Reset' }))
    await expect(await within(drawer).findByText('14 series')).toBeVisible()
  },
}

export const FilterDrawerChipAndRowAgree: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { 'filters-open': true } }),
  play: async ({ canvas, canvasElement, userEvent }) => {
    const drawer = await body(canvasElement).findByRole('dialog', { name: 'Filters' })
    await userEvent.click(await within(drawer).findByRole('button', { name: 'Ongoing', pressed: false }))
    await waitFor(() => expect(canvas.getByText(/ of 14$/)).toBeVisible())
    await userEvent.click(await within(drawer).findByRole('button', { name: /^Monitoring/ }))
    await userEvent.click(await within(drawer).findByRole('radio', { name: 'Unmonitored' }))
    await waitFor(() => expect(within(drawer).getByRole('button', { name: /^Monitoring Unmonitored/ })).toHaveFocus())
    await userEvent.keyboard('{Escape}')
    await expect(body(canvasElement).queryByRole('dialog', { name: 'Filters' })).toBeNull()
  },
}

export const FilterDrawerEscapeStepsBack: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { 'filters-open': true } }),
  play: async ({ canvasElement, userEvent }) => {
    const drawer = await body(canvasElement).findByRole('dialog', { name: 'Filters' })
    await userEvent.click(await within(drawer).findByRole('button', { name: /^Year/ }))
    await waitFor(() => expect(within(drawer).getByRole('button', { name: 'Back Year' })).toHaveFocus())
    await userEvent.keyboard('{Escape}')
    await waitFor(() => expect(within(drawer).getByRole('button', { name: /^Year/ })).toHaveFocus())
    await expect(body(canvasElement).getByRole('dialog', { name: 'Filters' })).toBeVisible()
  },
}

export const SavedFilterChips: Story = {
  play: async ({ canvas, userEvent }) => {
    const chip = await canvas.findByRole('button', { name: 'Ongoing and behind' })
    await userEvent.click(chip)
    await expect(await canvas.findByText('5 of 14')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Clear' })).toBeVisible()
  },
}

const onSaveFilter = fn()
const onDefaultFilter = fn()
const onReorderFilters = fn()
const onRenameFilter = fn()

export const SaveFilterDialog: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { status: 'Hiatus' } }),
  parameters: {
    msw: [
      http.post(api('/library/filters'), async ({ request }) => {
        const payload = (await request.json()) as { name: string }
        onSaveFilter(payload)
        return HttpResponse.json({ id: 99, name: payload.name, spec: {}, sortOrder: 3, isDefault: false })
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
    await userEvent.click(await canvas.findByRole('button', { name: 'Options for Ongoing and behind' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Delete' }))
    await expect(await body(canvasElement).findByText(/Deleted saved filter Ongoing and behind/)).toBeVisible()
    await expect(await body(canvasElement).findByRole('button', { name: 'Undo' })).toBeVisible()
  },
}

export const RenameSavedFilter: Story = {
  parameters: {
    msw: [
      http.put(api('/library/filters/:id'), async ({ params, request }) => {
        const payload = (await request.json()) as { name: string; spec: object }
        onRenameFilter({ id: Number(params.id), name: payload.name })
        return HttpResponse.json({ id: Number(params.id), name: payload.name, spec: payload.spec, sortOrder: 0, isDefault: false })
      }),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Options for Favourites' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Rename' }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Rename saved filter' })
    const input = await within(dialog).findByLabelText('Name')
    await userEvent.clear(input)
    await userEvent.type(input, 'Keepers')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Save' }))
    await waitFor(() => expect(onRenameFilter).toHaveBeenCalledWith({ id: 2, name: 'Keepers' }))
  },
}

export const SavedFilterAsDefault: Story = {
  parameters: {
    msw: [
      http.put(api('/library/filters/:id/default'), async ({ params, request }) => {
        const payload = (await request.json()) as { isDefault: boolean }
        onDefaultFilter({ id: Number(params.id), isDefault: payload.isDefault })
        return HttpResponse.json({ ...savedFilters[1], isDefault: payload.isDefault })
      }),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Options for Favourites' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Open the Library with this' }))
    await waitFor(() => expect(onDefaultFilter).toHaveBeenCalledWith({ id: 2, isDefault: true }))
  },
}

function LocationProbe() {
  const { search } = useLocation()
  return <output data-testid="location" hidden>{search}</output>
}

export const ViewWritesTheLink: Story = {
  decorators: [
    (Story) => (
      <>
        <LocationProbe />
        <Story />
      </>
    ),
  ],
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Ongoing and behind' }))
    const probe = () => canvasElement.ownerDocument.querySelector('[data-testid="location"]')
    await waitFor(() => expect(probe()?.textContent).toContain('status=Ongoing'))
    await waitFor(() => expect(probe()?.textContent).toContain('completeness=behind'))
    await waitFor(() => expect(probe()?.textContent).toContain('sort=incomplete'))
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear' }))
    await waitFor(() => expect(probe()?.textContent).toBe(''))
  },
}

export const ViewFromTheLink: Story = {
  beforeEach: () => seedLibraryPrefs({ page: { status: 'Hiatus' } }),
  parameters: { router: { initialEntries: ['/library?status=Completed&sort=title&sortDir=desc&bogus=1'] } },
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByText('4 of 14')).toBeVisible()
    await waitFor(() => expect(canvasElement.querySelector('.cover-title')).not.toBeNull())
  },
}

export const UnusableLinkParamsAreIgnored: Story = {
  parameters: { router: { initialEntries: ['/library?sort=bogus&status=&tagIds=x&readMin=80&readMax=20&yearMin=abc'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('link', { name: /Lanterns Over Kasumi/ })).toBeVisible()
    expect(canvas.queryByText(/ of 14$/)).toBeNull()
  },
}

export const DefaultSavedFilterApplies: Story = {
  parameters: {
    msw: [
      http.get(api('/library/filters'), () =>
        HttpResponse.json(savedFilters.map((f) => ({ ...f, isDefault: f.id === 1 }))),
      ),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('5 of 14')).toBeVisible()
  },
}

export const ReorderSavedFilters: Story = {
  parameters: {
    msw: [
      http.put(api('/library/filters/order'), async ({ request }) => {
        const payload = (await request.json()) as { ids: number[] }
        onReorderFilters(payload.ids)
        return HttpResponse.json(savedFilters)
      }),
      ...libraryHandlers,
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Options for Favourites' }))
    await userEvent.click(await body(canvasElement).findByRole('menuitem', { name: 'Move earlier' }))
    await waitFor(() => expect(onReorderFilters).toHaveBeenCalledWith([2, 1, 3]))
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

export const SelectRangeWithShift: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await enterSelectMode(canvas, userEvent)
    await waitFor(() => expect(canvasElement.querySelectorAll('.cover-card').length).toBeGreaterThan(5))
    const cards = () => [...canvasElement.querySelectorAll<HTMLElement>('.cover-card')]
    await userEvent.click(cards()[1])
    await expect(await canvas.findByText('1 selected')).toBeVisible()
    await userEvent.keyboard('{Shift>}')
    await userEvent.click(cards()[4])
    await userEvent.keyboard('{/Shift}')
    await expect(await canvas.findByText('4 selected')).toBeVisible()
    await waitFor(() => expect(cards().slice(1, 5).every((c) => c.getAttribute('aria-checked') === 'true')).toBe(true))
    await waitFor(() => expect(cards()[0]).toHaveAttribute('aria-checked', 'false'))
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
      ...bulkJobHandlers((id) => (id === 3 ? 'A download for this series is already running' : null)),
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
    const official = await within(dialog).findByRole('radio', { name: /^Official first/ })
    await expect(within(official).getByText(/Prefers official and volume releases/)).toBeVisible()
    await userEvent.click(official)
    await waitFor(() => expect(official).toHaveAttribute('aria-checked', 'true'))
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

import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import {
  LibraryFilterPanel,
  type FilterOption,
  type LibraryFilterOptions,
  type LibraryFilterSetters,
  type LibraryFilterValues,
} from './LibraryFilterPanel'

const opts = (...labels: string[]): FilterOption[] => labels.map((l) => ({ value: l, label: l }))
const counted = (...pairs: [string, number][]): FilterOption[] =>
  pairs.map(([name, n]) => ({ value: name, name, label: `${name} (${n})` }))

const OPTIONS: LibraryFilterOptions = {
  statuses: opts('Ongoing', 'Completed', 'Hiatus', 'Cancelled'),
  types: [
    { value: 'manga', label: 'Manga' },
    { value: 'manhwa', label: 'Manhwa' },
    { value: 'manhua', label: 'Manhua' },
  ],
  contentRatings: [
    { value: 'safe', label: 'Safe' },
    { value: 'suggestive', label: 'Suggestive' },
  ],
  readingStatuses: [
    { value: 'PlanToRead', label: 'Plan to read' },
    { value: 'Reading', label: 'Reading' },
    { value: 'OnHold', label: 'On hold' },
    { value: 'Dropped', label: 'Dropped' },
  ],
  credits: counted(['Aoi Mizuhara', 4], ['Ren Takeda', 2]),
  publishers: counted(['Lantern Press', 3]),
  tags: [
    { value: '1', name: 'Favourites', label: 'Favourites (5)' },
    { value: '2', name: 'Reread', label: 'Reread (2)' },
  ],
  genres: counted(
    ['Action', 9],
    ['Fantasy', 8],
    ['Romance', 6],
    ['Comedy', 5],
    ['Drama', 5],
    ['Mystery', 4],
    ['Slice of Life', 3],
    ['Sci-Fi', 3],
    ['Horror', 2],
    ['Sports', 1],
  ),
  metaTags: counted(['Time Travel', 3], ['Found Family', 2]),
  sources: counted(['MangaDex', 10], ['Comick', 4]),
  fileSources: counted(['MangaDex', 8]),
  rootFolders: [
    { value: '1', label: '/library/manga' },
    { value: '2', label: '/library/manhwa' },
  ],
  overrides: [
    { value: 'readFileCleanup', label: 'Read file cleanup' },
    { value: 'pageConversion', label: 'Page conversion' },
  ],
  qualityProfiles: [
    { value: 'all', label: 'Any' },
    { value: 'default', label: 'Instance default' },
  ],
  sourceStates: [
    { value: 'all', label: 'Any' },
    { value: 'none', label: 'No sources linked' },
  ],
  chapterModes: [
    { value: 'downloaded', label: 'Downloaded' },
    { value: 'total', label: 'Total' },
  ],
  ratingStates: [
    { value: 'all', label: 'Any' },
    { value: 'rated', label: 'Rated' },
    { value: 'unrated', label: 'Unrated' },
  ],
  matchModes: [
    { value: 'any', label: 'Any' },
    { value: 'all', label: 'All' },
  ],
}

const EMPTY: LibraryFilterValues = {
  status: 'all',
  types: [],
  contentRatings: [],
  readingStatuses: [],
  chapterMode: 'downloaded',
  chapterMin: null,
  chapterMax: null,
  yearMin: null,
  yearMax: null,
  addedWithin: null,
  credits: [],
  publishers: [],
  anime: 'all',
  clash: 'all',
  tags: [],
  tagMatch: 'any',
  excludeTags: [],
  untaggedOnly: false,
  genres: [],
  genreMatch: 'any',
  excludeGenres: [],
  metaTags: [],
  metaTagMatch: 'any',
  ratingState: 'all',
  ratingRange: [1, 10],
  communityRange: [0, 10],
  readRange: [0, 100],
  monitored: 'all',
  qualityProfile: 'all',
  completeness: 'all',
  sourceState: 'all',
  sources: [],
  sourceMatch: 'any',
  fileSources: [],
  fileSourceMatch: 'any',
  rootFolders: [],
  incognito: 'all',
  muted: 'all',
  overrides: [],
}

const onClose = fn()
const onManageTags = fn()

function Harness({ initial }: { initial?: Partial<LibraryFilterValues> }) {
  const [values, setValues] = useState<LibraryFilterValues>({ ...EMPTY, ...initial })
  const set = Object.fromEntries(
    Object.keys(EMPTY).map((key) => [key, (value: unknown) => setValues((v) => ({ ...v, [key]: value }))]),
  ) as LibraryFilterSetters
  const active = Object.entries(values).filter(([k, v]) => JSON.stringify(v) !== JSON.stringify(EMPTY[k as keyof LibraryFilterValues])).length
  return (
    <LibraryFilterPanel
      opened
      onClose={onClose}
      values={values}
      set={set}
      options={OPTIONS}
      hasCommunityRatings
      readTracking
      showRootFolder
      shownCount={Math.max(0, 14 - active * 3)}
      activeCount={active}
      onReset={() => setValues(EMPTY)}
      onManageTags={onManageTags}
    />
  )
}

/** The Library's filter drawer on the drill grammar: chip rows, then one row per filter that opens its page. */
const meta = {
  component: Harness,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
} satisfies Meta<typeof Harness>

export default meta
type Story = StoryObj<typeof meta>

const drawerOf = async (canvasElement: HTMLElement) =>
  within(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Filters' }))

export const Root: Story = {
  args: { initial: { status: 'Ongoing', readingStatuses: ['Reading'], yearMin: 2010, yearMax: 2024 } },
  play: async ({ canvasElement, userEvent }) => {
    const drawer = await drawerOf(canvasElement)
    await waitFor(async () => expect(await drawer.findByRole('group', { name: 'Status' })).toBeVisible())
    await waitFor(async () => expect(await drawer.findByRole('button', { name: 'Ongoing', pressed: true })).toBeVisible())
    await waitFor(async () => expect(await drawer.findByRole('button', { name: /^Year 2010 to 2024/ })).toBeVisible())
    await waitFor(async () => expect(await drawer.findByRole('button', { name: /^Content rating Any/ })).toBeVisible())
    await userEvent.click(await drawer.findByRole('button', { name: 'Fantasy', pressed: false }))
    await waitFor(async () => expect(await drawer.findByRole('button', { name: /^Genres Fantasy/ })).toBeVisible())
    await userEvent.click(await drawer.findByRole('button', { name: 'Done' }))
    await waitFor(() => expect(onClose).toHaveBeenCalled())
  },
}

export const SinglePickPage: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const drawer = await drawerOf(canvasElement)
    await userEvent.click(await drawer.findByRole('button', { name: /^Completeness/ }))
    await waitFor(async () => expect(await drawer.findByRole('radiogroup', { name: 'Completeness' })).toBeVisible())
    await userEvent.click(await drawer.findByRole('radio', { name: 'Complete' }))
    await waitFor(() => expect(drawer.getByRole('button', { name: /^Completeness Complete/ })).toHaveFocus())
  },
}

export const GenresIncludeAndLeaveOut: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const drawer = await drawerOf(canvasElement)
    await userEvent.click(await drawer.findByRole('button', { name: /^Genres/ }))
    await waitFor(async () => expect(await drawer.findByRole('textbox', { name: 'Search Genres' })).toBeVisible())
    const box = (name: RegExp) => drawer.getByRole('checkbox', { name })
    await userEvent.click(await drawer.findByRole('checkbox', { name: /^Horror/ }))
    await waitFor(() => expect(box(/^Horror/)).toHaveAttribute('aria-checked', 'true'))
    await userEvent.click(box(/^Horror/))
    await waitFor(() => expect(box(/^Horror/)).toHaveAttribute('aria-checked', 'mixed'))
    await expect(box(/^Horror/)).toHaveAccessibleName(/Left out/)
    await userEvent.click(box(/^Action/))
    await waitFor(() => expect(box(/^Action/)).toHaveAttribute('aria-checked', 'true'))
    await userEvent.click(box(/^Drama/))
    await waitFor(() => expect(box(/^Drama/)).toHaveAttribute('aria-checked', 'true'))
    await expect(await drawer.findByRole('radio', { name: 'All' })).toBeInTheDocument()
    await userEvent.type(await drawer.findByRole('textbox', { name: 'Search Genres' }), 'sci')
    await waitFor(() => expect(drawer.getAllByRole('checkbox')).toHaveLength(1))
    await userEvent.click(await drawer.findByRole('button', { name: 'Back Genres' }))
    await waitFor(async () => expect(await drawer.findByRole('button', { name: /^Genres 2 in, 1 out/ })).toBeVisible())
  },
}

export const RangePage: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const drawer = await drawerOf(canvasElement)
    await userEvent.click(await drawer.findByRole('button', { name: /^Year/ }))
    await userEvent.type(await drawer.findByRole('textbox', { name: 'Earliest year' }), '2015')
    await userEvent.click(await drawer.findByRole('button', { name: 'Back Year' }))
    await waitFor(async () => expect(await drawer.findByRole('button', { name: /^Year From 2015/ })).toBeVisible())
    await userEvent.click(await drawer.findByRole('button', { name: /^Read Any/ }))
    await waitFor(async () => expect(await drawer.findByRole('slider', { name: 'Read from' })).toBeVisible())
    await waitFor(async () => expect(await drawer.findByRole('slider', { name: 'Read to' })).toBeVisible())
  },
}

export const YourTagsPage: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const drawer = await drawerOf(canvasElement)
    await userEvent.click(await drawer.findByRole('button', { name: /^Your tags/ }))
    await userEvent.click(await drawer.findByRole('switch', { name: /Untagged only/ }))
    await waitFor(() => expect(drawer.getByRole('checkbox', { name: /^Favourites/ })).toBeDisabled())
    await userEvent.click(await drawer.findByRole('button', { name: 'Manage tags' }))
    await waitFor(() => expect(onManageTags).toHaveBeenCalled())
  },
}

export const PhoneWidth: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvasElement }) => {
    const doc = canvasElement.ownerDocument
    const dialog = await within(doc.body).findByRole('dialog', { name: 'Filters' })
    const content = dialog.closest('.library-filter-drawer') ?? dialog
    await waitFor(() => expect(Math.round(content.getBoundingClientRect().width)).toBe(doc.documentElement.clientWidth))
    await expect(doc.documentElement.scrollWidth).toBeLessThanOrEqual(doc.documentElement.clientWidth)
    await waitFor(async () => expect(await within(dialog).findByRole('button', { name: 'Done' })).toBeVisible())
  },
}

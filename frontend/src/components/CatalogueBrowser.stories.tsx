import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { Text } from '@mantine/core'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { catalogueItem, discoverPage, resetTabState, searchDefaults } from '../../.storybook/mocks/discover'
import { CatalogueBrowser, PosterSkeletons, Results } from './CatalogueBrowser'
import { POSTER_COLS_BY_DENSITY, type ViewPrefs } from './ui/viewPrefs'

const meta = {
  component: CatalogueBrowser,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  args: { scope: 'discover-catalogue' },
  parameters: { layout: 'padded', router: { initialEntries: ['/add'] }, msw: discoverPage() },
} satisfies Meta<typeof CatalogueBrowser>

export default meta
type Story = StoryObj<typeof meta>

const searchWith = (body: object) =>
  discoverPage(http.post(api('/recommendations/discover/search'), () => HttpResponse.json(body)))

export const Browsing: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Browsing the catalogue')).toBeVisible()
    await expect((await canvas.findAllByRole('button', { name: /^View and add / })).length).toBeGreaterThan(5)
  },
}

export const SearchResults: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'salt')
    await expect(await canvas.findByText('1 match', {}, { timeout: 4000 })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'View and add The Salt Cartographer' })).toBeVisible()
  },
}

export const SearchByCreator: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'Mio Tachibana')
    await expect(await canvas.findByRole('link', { name: /Mio Tachibana \(\d+\)/ }, { timeout: 4000 })).toBeVisible()
  },
}

export const NothingFound: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'zzzqqq')
    await expect(await canvas.findByText('No matches', {}, { timeout: 4000 })).toBeVisible()
  },
}

export const SpellingCorrected: Story = {
  parameters: { msw: searchWith({ mode: 'title', items: [catalogueItem(0)], correctedQuery: 'The Salt Cartographer' }) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'salt cartogrpher')
    await expect(await canvas.findByText(/showing results for/, {}, { timeout: 4000 })).toBeVisible()
  },
}

export const SmartSearchUnavailableForAdmin: Story = {
  parameters: { msw: searchWith({ mode: 'title', items: [catalogueItem(0)], fallback: 'unavailable' }) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'salt')
    await expect(await canvas.findByText(/search by meaning needs its index/, {}, { timeout: 4000 })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Download index' })).toBeVisible()
  },
}

export const SmartSearchUnavailableForReader: Story = {
  parameters: { auth: 'reader', msw: searchWith({ mode: 'title', items: [catalogueItem(0)], fallback: 'unavailable' }) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'salt')
    await expect(await canvas.findByText(/an admin can set up search by meaning/, {}, { timeout: 4000 })).toBeVisible()
  },
}

export const SmartSearchWarming: Story = {
  parameters: { msw: searchWith({ mode: 'title', items: [catalogueItem(0)], fallback: 'warming' }) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'salt')
    await expect(await canvas.findByText(/loading search by meaning/, {}, { timeout: 4000 })).toBeVisible()
  },
}

export const SmartSearchPreparing: Story = {
  parameters: { msw: searchWith({ mode: 'title', items: [catalogueItem(0)], fallback: 'preparing' }) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'salt')
    await expect(await canvas.findByText(/search by meaning is being set up/, {}, { timeout: 4000 })).toBeVisible()
  },
}

export const SmartSearchOff: Story = {
  parameters: { msw: searchWith({ mode: 'title', items: [catalogueItem(0)], fallback: 'off' }) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'salt')
    await expect(await canvas.findByText(/search by meaning is off/, {}, { timeout: 4000 })).toBeVisible()
  },
}

export const FiltersPanel: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Filters' }))
    await expect(await canvas.findByText('Genres and tags')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Save as default' })).toBeVisible()
  },
}

export const SaveFiltersAsDefault: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Filters' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Save as default' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText('Default cleared')).toBeVisible()
  },
}

export const SavedDefaultApplied: Story = {
  parameters: {
    msw: discoverPage(
      http.get(api('/recommendations/discover/searchdefaults'), () =>
        HttpResponse.json({ ...searchDefaults, types: ['manhwa'], minRating: 70 }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Filters (2)' })).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: discoverPage(http.post(api('/recommendations/discover/feed'), () => neverResolve())) },
}

export const BrowseFailed: Story = {
  parameters: {
    msw: discoverPage(http.post(api('/recommendations/discover/feed'), () => failWith(500, 'The catalogue is unavailable'))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('The catalogue is unavailable')).toBeVisible()
  },
}

export const NoLocalCatalogue: Story = {
  parameters: { msw: discoverPage(http.post(api('/recommendations/discover/feed'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing here')).toBeVisible()
  },
}

export const WithIdleContent: Story = {
  args: { idle: <Text>Curated rails would sit here until somebody starts typing.</Text> },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Curated rails would sit here/)).toBeVisible()
  },
}

export const SearchHidden: Story = {
  args: { hideSearch: true, idle: <Text>Only the idle content, no search box.</Text> },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('textbox')).toBeNull()
  },
}

const gridPrefs: ViewPrefs = {
  viewMode: 'grid',
  setViewMode: () => {},
  density: 'default',
  setDensity: () => {},
  cols: POSTER_COLS_BY_DENSITY.default,
}

export const ResultsGrid: StoryObj = {
  render: () => (
    <Results
      items={Array.from({ length: 12 }, (_, i) => catalogueItem(i))}
      prefs={gridPrefs}
      seriesIdFor={() => null}
      onOpen={() => {}}
    />
  ),
}

export const ResultsList: StoryObj = {
  render: () => (
    <Results
      items={Array.from({ length: 6 }, (_, i) => catalogueItem(i))}
      prefs={{ ...gridPrefs, viewMode: 'list' }}
      seriesIdFor={(item) => (item.providerId === '30001' ? 4 : null)}
      onOpen={() => {}}
    />
  ),
}

export const SkeletonGrid: StoryObj = {
  render: () => <PosterSkeletons density="default" />,
}

export const SkeletonList: StoryObj = {
  render: () => <PosterSkeletons density="comfortable" viewMode="list" />,
}

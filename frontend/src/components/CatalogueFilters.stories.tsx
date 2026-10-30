import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { Panel } from './ui/Panel'
import { discoverPage, resetTabState } from '../../.storybook/mocks/discover'
import type { RecommendationFilters } from '../api/hooks'
import {
  CatalogueFilterActions,
  CatalogueFilters,
  useCatalogueFilters,
  type CatalogueFilterControls,
} from './CatalogueFilters'

interface HarnessArgs {
  initial?: RecommendationFilters
  collapsed?: boolean
}

function Harness({ initial, collapsed }: HarnessArgs) {
  const filters = useCatalogueFilters(initial)
  return (
    <Panel p="md">
      <CatalogueFilters controls={filters.controls} collapsed={collapsed} />
    </Panel>
  )
}

const meta = {
  component: CatalogueFilters,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  // The controls come from the hook inside the harness; this keeps the required prop satisfied.
  args: { controls: undefined as unknown as CatalogueFilterControls },
  render: (args, { parameters }) => <Harness collapsed={args.collapsed} initial={parameters.initial as RecommendationFilters | undefined} />,
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof CatalogueFilters>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Genres and tags')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: /Genres and tags/ })).toHaveAttribute('aria-expanded', 'true'))
  },
}

export const Collapsed: Story = {
  args: { collapsed: true },
  play: async ({ canvas, userEvent }) => {
    const series = await canvas.findByRole('button', { name: /^Series/ })
    await waitFor(() => expect(series).toHaveAttribute('aria-expanded', 'false'))
    await userEvent.click(series)
    await waitFor(() => expect(series).toHaveAttribute('aria-expanded', 'true'))
    await expect(await canvas.findByText('Creators and studios')).toBeVisible()
  },
}

export const WithActiveFilters: Story = {
  args: { collapsed: true },
  parameters: {
    initial: {
      types: ['manhwa'],
      statuses: ['releasing'],
      minRating: 70,
      minChapters: 20,
      maxChapters: 200,
      yearMin: 2018,
      rules: [
        { mode: 'all', terms: [{ kind: 'genre', name: 'Fantasy' }] },
        { mode: 'none', terms: [{ kind: 'tag', name: 'Gore' }] },
      ],
      credits: [{ name: 'Studio Lark', role: 'studio' }],
    } satisfies RecommendationFilters,
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Studio Lark')).toBeVisible()
    await expect(await canvas.findByText(/Rated ★ 7\.0 or more/)).toBeVisible()
  },
}

export const ActionsIdle: StoryObj = {
  render: () => <CatalogueFilterActions isCustomized={false} onReset={() => {}} onApply={() => {}} onSaveAsDefault={() => {}} />,
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Reset' })).toBeDisabled())
  },
}

export const ActionsCustomizedAndSaving: StoryObj = {
  render: () => (
    <CatalogueFilterActions
      isCustomized
      saving
      onReset={() => {}}
      onApply={() => {}}
      onSaveAsDefault={() => {}}
      extra={<span>128 series match</span>}
    />
  ),
}

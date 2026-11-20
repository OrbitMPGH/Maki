import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { discoverPage, resetTabState } from '../../.storybook/mocks/discover'
import type { RecommendationFilters } from '../api/hooks'
import { CatalogueFilterBar, useCatalogueFilterSlots } from './CatalogueFilterBar'
import { useCatalogueFilters } from './CatalogueFilters'

function Harness({ initial, menu }: { initial?: RecommendationFilters; menu?: boolean }) {
  const filters = useCatalogueFilters(initial)
  const slots = useCatalogueFilterSlots(filters.controls, { creators: true })
  return (
    <CatalogueFilterBar slots={slots} isCustomized={filters.isCustomized} onReset={filters.reset} menu={menu} />
  )
}

const meta = {
  component: CatalogueFilterBar,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  // The slots come from the hooks inside the harness; this keeps the required props satisfied.
  args: { slots: [], isCustomized: false, onReset: () => {} },
  render: (args, { parameters }) => (
    <Harness initial={parameters.initial as RecommendationFilters | undefined} menu={args.menu} />
  ),
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof CatalogueFilterBar>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Genres' }))
    await expect(await within(canvasElement.ownerDocument.body).findByText('Genres and tags')).toBeVisible()
  },
}

/** Inside an editor (the custom rail form) the bar has no overflow menu. */
export const WithoutMenu: Story = {
  args: { menu: false },
  play: async ({ canvas }) => {
    await canvas.findByRole('button', { name: 'Genres' })
    await expect(canvas.queryByRole('button', { name: 'More filter actions' })).toBeNull()
  },
}

export const WithActiveFilters: Story = {
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
    await expect(await canvas.findByText('7.0+')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Reset' })).toBeVisible()
  },
}

/** A preset saved at 5.0, from before the uneven stops, keeps its value until the slider moves. */
export const RatingStops: Story = {
  parameters: { initial: { minRating: 50 } satisfies RecommendationFilters },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('button', { name: /5\.0\+/ }))
    const thumb = await body.findByRole('slider', { name: 'Minimum rating' })
    thumb.focus()
    await userEvent.keyboard('{ArrowRight}')
    await expect(await canvas.findByText('6.8+')).toBeVisible()
    await expect(await body.findByText('Top 76% of popular series')).toBeVisible()
  },
}

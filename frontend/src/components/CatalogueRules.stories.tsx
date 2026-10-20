import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../.storybook/mocks/api'
import { discoverPage, resetTabState } from '../../.storybook/mocks/discover'
import type { CatalogueTerm, RecommendationFilters } from '../api/hooks'
import { FilterMatchCount, TermFilters, TermPicker, useTermFilters } from './CatalogueRules'

function Harness({ initial }: { initial?: RecommendationFilters }) {
  const terms = useTermFilters(null, initial)
  return <TermFilters controls={terms} />
}

const meta = {
  component: TermFilters,
  tags: ['ai-generated'],
  beforeEach: resetTabState,
  args: { controls: undefined as never },
  render: (_args, { parameters }) => <Harness initial={parameters.initial as RecommendationFilters | undefined} />,
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof TermFilters>

export default meta
type Story = StoryObj<typeof meta>

export const SimpleView: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('radio', { name: 'Simple' })).toBeChecked())
  },
}

export const SimpleWithSelections: Story = {
  parameters: {
    initial: {
      rules: [
        { mode: 'all', terms: [{ kind: 'genre', name: 'Fantasy' }] },
        { mode: 'all', terms: [{ kind: 'tag', name: 'Found Family' }, { kind: 'tag', name: 'Travel' }] },
        { mode: 'none', terms: [{ kind: 'genre', name: 'Horror' }, { kind: 'tag', name: 'Gore' }] },
      ],
    } satisfies RecommendationFilters,
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Found Family')).toBeVisible()
  },
}

export const RulesView: Story = {
  parameters: {
    initial: {
      rules: [
        { mode: 'any', terms: [{ kind: 'genre', name: 'Fantasy' }, { kind: 'genre', name: 'Sci-Fi' }] },
        { mode: 'all', terms: [{ kind: 'tag', name: 'School', subtags: true }] },
        { mode: 'none', terms: [{ kind: 'tag', name: 'Gore', central: true }] },
      ],
    } satisfies RecommendationFilters,
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('radio', { name: 'Rules' })).toBeChecked())
  },
}

export const SwitchToRules: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByText('Rules'))
    await waitFor(() => expect(canvas.getByRole('radio', { name: 'Rules' })).toBeChecked())
  },
}

function PickerHarness({ tone, initial }: { tone: 'include' | 'exclude'; initial?: CatalogueTerm[] }) {
  const [value, setValue] = useState<CatalogueTerm[]>(initial ?? [])
  return (
    <div style={{ maxWidth: 480 }}>
      <TermPicker label="Tags" kinds={['genre', 'tag']} tone={tone} value={value} onChange={setValue} />
    </div>
  )
}

export const PickerInclude: StoryObj = {
  render: () => <PickerHarness tone="include" />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('textbox'))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('option', { name: /Cooking/ }))
    await expect(await canvas.findByText('Cooking')).toBeVisible()
  },
}

export const PickerExclude: StoryObj = {
  render: () => (
    <PickerHarness
      tone="exclude"
      initial={[
        { kind: 'genre', name: 'Horror' },
        { kind: 'tag', name: 'Gore' },
      ]}
    />
  ),
}

export const PickerSearch: StoryObj = {
  render: () => <PickerHarness tone="include" />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox'), 'det')
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('option', { name: /Detective/ })).toBeInTheDocument()
  },
}

export const MatchCount: StoryObj = {
  render: () => <FilterMatchCount filters={{ types: ['manhwa'] }} />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('128 series match', {}, { timeout: 3000 })).toBeVisible()
  },
}

export const MatchCountSingular: StoryObj = {
  render: () => <FilterMatchCount filters={{ credits: [{ name: 'Studio Lark', role: 'studio' }] }} />,
  parameters: {
    msw: discoverPage(http.post(api('/recommendations/discover/count'), () => HttpResponse.json({ count: 1 }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('1 series matches', {}, { timeout: 3000 })).toBeVisible()
  },
}

export const MatchCountPending: StoryObj = {
  render: () => <FilterMatchCount filters={{ types: ['manhwa'] }} />,
  parameters: { msw: discoverPage(http.post(api('/recommendations/discover/count'), () => neverResolve())) },
}

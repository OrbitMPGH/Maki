import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../.storybook/mocks/api'
import { libraryHandlers, recommendationTags } from '../../.storybook/mocks/library'
import type { CatalogueTerm, TagOption } from '../api/hooks'
import { TagBrowserModal } from './TagBrowserModal'

// Discover answers this endpoint too; pinning a copy here keeps these stories independent of it.
const withTags = (tags: TagOption[] = recommendationTags) => [
  http.get(api('/recommendations/tags'), () => HttpResponse.json(tags)),
  ...libraryHandlers,
]

const meta = {
  component: TagBrowserModal,
  tags: ['ai-generated'],
  args: {
    opened: true,
    onClose: fn(),
    kinds: ['tag'],
    tone: 'include',
    value: [],
    onChange: fn(),
  },
  parameters: { layout: 'fullscreen', msw: withTags() },
  render: function Render(args) {
    const [value, setValue] = useState<CatalogueTerm[]>(args.value)
    return (
      <TagBrowserModal
        {...args}
        value={value}
        onChange={(terms) => {
          setValue(terms)
          args.onChange(terms)
        }}
      />
    )
  },
} satisfies Meta<typeof TagBrowserModal>

export default meta
type Story = StoryObj<typeof meta>

const modal = (canvasElement: HTMLElement, name: string) =>
  within(canvasElement.ownerDocument.body).findByRole('dialog', { name })

export const IncludeTags: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await modal(canvasElement, 'Browse tags to include')
    await userEvent.click(await within(dialog).findByRole('checkbox', { name: 'Include Cooking' }))
    await expect(args.onChange).toHaveBeenCalledWith([{ kind: 'tag', name: 'Cooking' }])
    await expect(within(dialog).getByText('1 selected')).toBeVisible()
  },
}

export const ExcludeTags: Story = {
  args: { tone: 'exclude', value: [{ kind: 'tag', name: 'Cooking' }] },
  play: async ({ canvasElement }) => {
    const dialog = await modal(canvasElement, 'Browse tags to exclude')
    await expect(await within(dialog).findByRole('checkbox', { name: 'Remove Cooking' })).toBeChecked()
  },
}

export const GenresAndTags: Story = {
  args: { kinds: ['genre', 'tag'] },
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await modal(canvasElement, 'Browse tags to include')
    const open = await within(dialog).findAllByRole('button', { name: 'Open Genres' })
    await userEvent.click(open[0])
    await expect(await within(dialog).findByRole('checkbox', { name: 'Include Fantasy' })).toBeVisible()
  },
}

export const BrowseIntoGroup: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await modal(canvasElement, 'Browse tags to include')
    const open = await within(dialog).findAllByRole('button', { name: 'Open Character Traits' })
    await userEvent.click(open[0])
    await expect(await within(dialog).findByRole('checkbox', { name: 'Include Stoic' })).toBeVisible()
    await expect(within(dialog).getByRole('button', { name: 'All tags' })).toBeVisible()
  },
}

export const SearchResults: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await modal(canvasElement, 'Browse tags to include')
    await userEvent.type(within(dialog).getByRole('textbox'), 'school')
    await expect(await within(dialog).findByRole('checkbox', { name: 'Include College' })).toBeVisible()
  },
}

export const SearchNoMatches: Story = {
  play: async ({ canvasElement, userEvent }) => {
    const dialog = await modal(canvasElement, 'Browse tags to include')
    await userEvent.type(within(dialog).getByRole('textbox'), 'qqqqq')
    await expect(await within(dialog).findByText('No matches')).toBeVisible()
  },
}

export const Loading: Story = {
  parameters: { msw: [http.get(api('/recommendations/tags'), () => neverResolve()), ...libraryHandlers] },
}

export const IndexNotBuilt: Story = {
  parameters: { msw: withTags([]) },
  play: async ({ canvasElement }) => {
    const dialog = await modal(canvasElement, 'Browse tags to include')
    await expect(await within(dialog).findByText('Tags appear once the recommendation index is built')).toBeVisible()
  },
}

export const Closed: Story = {
  args: { opened: false },
}

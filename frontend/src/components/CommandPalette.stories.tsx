import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../.storybook/mocks/api'
import { paletteSeries } from '../../.storybook/mocks/shell/series'
import { ALL_ITEMS } from '../nav'
import CommandPalette from './CommandPalette'

const INPUT_NAME = 'Jump to a series, page or setting…'

const meta = {
  component: CommandPalette,
  tags: ['ai-generated'],
  args: { navItems: ALL_ITEMS },
  parameters: {
    layout: 'centered',
    msw: [http.get(api('/series'), () => HttpResponse.json(paletteSeries))],
  },
} satisfies Meta<typeof CommandPalette>

export default meta
type Story = StoryObj<typeof meta>

export const Closed: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: /^Search/ })).toBeVisible()
    await expect(await canvas.findByText('Search')).toBeVisible()
  },
}

export const Opened: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    const input = await body.findByRole('combobox', { name: INPUT_NAME })
    await waitFor(() => expect(input).toHaveFocus())
    // With nothing typed the palette lists every page.
    await waitFor(async () => expect(await body.findByRole('option', { name: /Library/ })).toBeVisible())
    await waitFor(() => expect(body.getAllByRole('option')).toHaveLength(ALL_ITEMS.length))
  },
}

export const OpenedWithShortcut: Story = {
  play: async ({ canvasElement, userEvent }) => {
    await userEvent.keyboard('{Control>}k{/Control}')
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByRole('combobox', { name: INPUT_NAME })).toBeVisible())
  },
}

export const SearchSeries: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.type(await body.findByRole('combobox', { name: INPUT_NAME }), 'moon')
    await waitFor(async () => expect(await body.findByRole('option', { name: /Paper Moon Detective/ })).toBeVisible())
    // The status sits under the title, and the last row hands the text to Add series.
    await expect(await within(await body.findByRole('option', { name: /Paper Moon Detective/ })).findByText('Hiatus')).toBeVisible()
    await expect(await body.findByRole('option', { name: /Search for “moon”/ })).toBeVisible()
  },
}

export const SearchSetting: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.type(await body.findByRole('combobox', { name: INPUT_NAME }), 'appearance')
    await waitFor(() => expect(body.getAllByRole('option').some((o) => /Settings ›/.test(o.textContent ?? ''))).toBe(true))
  },
}

export const ArrowKeysMoveSelection: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    const input = await body.findByRole('combobox', { name: INPUT_NAME })
    await userEvent.type(input, 'a')
    const options = await body.findAllByRole('option')
    await waitFor(() => expect(options[0]).toHaveAttribute('aria-selected', 'true'))
    await userEvent.keyboard('{ArrowDown}')
    await waitFor(() => expect(body.getAllByRole('option')[1]).toHaveAttribute('aria-selected', 'true'))
    await userEvent.keyboard('{ArrowUp}{ArrowUp}')
    await waitFor(() => expect(body.getAllByRole('option').at(-1)).toHaveAttribute('aria-selected', 'true'))
  },
}

export const NoSeriesMatch: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.type(await body.findByRole('combobox', { name: INPUT_NAME }), 'zzzqx')
    // A title that is not in the library still gets a way into the Add page.
    const options = await body.findAllByRole('option')
    await waitFor(() => expect(options).toHaveLength(1))
    await waitFor(() => expect(options[0]).toHaveTextContent('Search for “zzzqx”'))
    await waitFor(() => expect(options[0]).toHaveTextContent('Add series'))
  },
}

export const ReaderSeesRequest: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.type(await body.findByRole('combobox', { name: INPUT_NAME }), 'zzzqx')
    await waitFor(() => expect(body.getByRole('option', { name: /Search for/ })).toHaveTextContent('Request series'))
  },
}

export const LibraryStillLoading: Story = {
  args: { navItems: [] },
  parameters: { msw: [http.get(api('/series'), () => neverResolve())] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    await body.findByRole('combobox', { name: INPUT_NAME })
    await expect(body.queryByText('No matches.')).toBeNull()
  },
}

export const NoMatches: Story = {
  args: { navItems: [] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText('No matches.')).toBeVisible())
  },
}

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
    // With nothing typed the palette lists every page, Notifications included.
    await waitFor(async () => expect(await body.findByRole('option', { name: /Library/ })).toBeVisible())
    await waitFor(() => expect(body.getAllByRole('option')).toHaveLength(ALL_ITEMS.length + 1))
    await expect(body.getByRole('option', { name: /Notifications/ })).toBeVisible()
  },
}

export const OpenedWithShortcut: Story = {
  play: async ({ canvasElement, userEvent }) => {
    await userEvent.keyboard('{Control>}k{/Control}')
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByRole('combobox', { name: INPUT_NAME })).toBeVisible())
  },
}

/** The shortcut still opens the palette while the cursor sits in a text field elsewhere on the page. */
export const OpenedWithShortcutFromField: Story = {
  decorators: [
    (Story) => (
      <>
        <input aria-label="Filter library" />
        <Story />
      </>
    ),
  ],
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('textbox', { name: 'Filter library' }))
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

/** A match through another title shows that title under the shown one, with the match marked. */
export const SearchByOtherTitle: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.type(await body.findByRole('combobox', { name: INPUT_NAME }), '紙の月')
    const option = await body.findByRole('option', { name: /Paper Moon Detective/ })
    await expect(await within(option).findByText('紙の月')).toHaveProperty('tagName', 'MARK')
    await expect(option).toHaveTextContent('紙の月探偵')
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

// Notifications has no sidebar item, so it is listed even when the caller passes no pages.
export const NotificationsWithoutNavItems: Story = {
  args: { navItems: [] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Search/ }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByRole('option', { name: /Notifications/ })).toBeVisible())
    await waitFor(() => expect(body.getAllByRole('option')).toHaveLength(1))
  },
}

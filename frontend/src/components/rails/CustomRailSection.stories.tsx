import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { customRails, homePage } from '../../../.storybook/mocks/home'
import { AddRailButton, CustomRailSection } from './CustomRailSection'

const meta = {
  component: CustomRailSection,
  tags: ['ai-generated'],
  args: { rail: customRails[0], limit: 20, onOpen: fn() },
  parameters: { layout: 'padded', msw: homePage() },
} satisfies Meta<typeof CustomRailSection>

export default meta
type Story = StoryObj<typeof meta>

export const LibrarySource: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Unread favourites' })).toBeVisible()
    await expect(await canvas.findByRole('link', { name: /Summer at Shiokaze Station|Shiokaze/ })).toBeVisible()
  },
}

export const RecommendationsSource: Story = {
  args: { rail: customRails[1] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Cozy picks' })).toBeVisible()
    await expect(await canvas.findAllByRole('button', { name: /View and add/ })).not.toHaveLength(0)
  },
}

export const CatalogueSource: Story = {
  args: { rail: customRails[2] },
  play: async ({ args, canvas, userEvent }) => {
    const cards = await canvas.findAllByRole('button', { name: /View and add Hollow Orchard Postman/ })
    await userEvent.click(cards[0])
    await expect(args.onOpen).toHaveBeenCalledOnce()
  },
}

export const Loading: Story = {
  parameters: { msw: homePage(http.get(api('/rails/:id/items'), () => neverResolve())) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Unread favourites' })).toBeVisible()
  },
}

export const NothingMatches: Story = {
  parameters: {
    msw: homePage(
      http.get(api('/rails/:id/items'), () =>
        HttpResponse.json({ source: 'library', items: [], seriesIds: [] }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing matches this rail right now.')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Show more' })).toBeNull()
  },
}

export const NeedsLocalDatabase: Story = {
  args: { rail: customRails[2] },
  parameters: {
    msw: homePage(
      http.get(api('/rails/:id/items'), () =>
        HttpResponse.json({ source: 'catalogue', items: [], seriesIds: [], unavailable: 'catalogue' }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/needs the local MangaBaka database/)).toBeVisible()
  },
}

export const LoadFailed: Story = {
  parameters: { msw: homePage(http.get(api('/rails/:id/items'), () => failWith(500))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/couldn't load this rail/i)).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Retry' })).toBeVisible()
  },
}

export const OptionsMenu: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Rail options' }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(() => expect(body.getByRole('menuitem', { name: 'Edit' })).toBeVisible())
    await expect(body.getByRole('menuitem', { name: 'Duplicate' })).toBeVisible()
    // A Home library rail can't be copied to Discover, which has no library source.
    await expect(body.queryByRole('menuitem', { name: 'Copy to Discover' })).toBeNull()
  },
}

export const CatalogueMenuOffersCopyToDiscover: Story = {
  args: { rail: customRails[2] },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Rail options' }))
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(() => expect(body.getByRole('menuitem', { name: 'Copy to Discover' })).toBeVisible())
  },
}

export const EditOpensEditor: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Rail options' }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('menuitem', { name: 'Edit' }))
    await waitFor(async () => expect(await body.findByRole('dialog', { name: 'Edit rail' })).toBeVisible())
  },
}

export const DeleteAsksFirst: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Rail options' }))
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await body.findByRole('menuitem', { name: 'Delete' }))
    await waitFor(async () => expect(await body.findByRole('dialog', { name: 'Delete rail' })).toBeVisible())
  },
}

export const ShowMoreOpensLibraryGrid: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Show more' }))
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Unread favourites' })
    await waitFor(() => expect(within(dialog).getAllByRole('link').length).toBeGreaterThan(0))
  },
}

export const AddRail: Story = {
  render: () => <AddRailButton placement="home" />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'Add a rail' }))
    await waitFor(async () => expect(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'New rail' })).toBeVisible())
  },
}

export const AddRailCustomLabel: Story = {
  render: () => <AddRailButton placement="discover" label="Create a rail" />,
}

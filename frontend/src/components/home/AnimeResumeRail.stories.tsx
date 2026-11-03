import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { animeResumeAllPage, animeResumeItems } from '../../../.storybook/mocks/home'
import { AnimeResumeExpandModal, AnimeResumeRail } from './AnimeResumeRail'

const meta = {
  component: AnimeResumeRail,
  tags: ['ai-generated'],
  args: { items: animeResumeItems, onOpen: fn() },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof AnimeResumeRail>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('link', { name: /Clockwork Tea House/ })).toHaveAttribute('href', '/series/7'))
  },
}

export const OpensCatalogueEntry: Story = {
  play: async ({ args, canvas, userEvent }) => {
    // A manga that is not in the library opens the detail modal through onOpen instead of linking.
    await userEvent.click(await canvas.findByRole('button', { name: /View and add The Salt Cartographer/ }))
    await waitFor(() => expect(args.onOpen).toHaveBeenCalledWith(animeResumeItems[3].catalogue))
  },
}

export const HideUntilNewSeason: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('button', { name: 'More options for Clockwork Tea House' }))
    await userEvent.click(await body.findByRole('menuitem', { name: 'Hide until a new season' }))
    await expect(await body.findByText(/Hid Clockwork Tea House\. It comes back when you finish a new season\./)).toBeVisible()
    await expect(await body.findByRole('button', { name: 'Undo' })).toBeVisible()
  },
}

const dismissed = new Set<number>()

export const NotReadingFromExpandedModal: Story = {
  beforeEach: () => dismissed.clear(),
  parameters: {
    msw: [
      http.get(api('/home/from-anime'), () => {
        const items = animeResumeAllPage.items.filter((i) => !dismissed.has(i.mangaBakaId))
        return HttpResponse.json({ items, total: items.length })
      }),
      http.post(api('/home/from-anime/:mangaBakaId/dismiss'), ({ params }) => {
        dismissed.add(Number(params.mangaBakaId))
        return new HttpResponse(null, { status: 204 })
      }),
    ],
  },
  render:(args) => <AnimeResumeExpandModal opened onClose={fn()} onOpen={args.onOpen} />,
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    // An unadded manga can be taken off too; the grid drops it at once.
    await userEvent.click(await body.findByRole('button', { name: 'More options for The Salt Cartographer' }))
    await userEvent.click(await body.findByRole('menuitem', { name: 'Not reading this one' }))
    await expect(await body.findByText(/It won't be suggested here again\./)).toBeVisible()
    await waitFor(() => expect(body.queryByRole('button', { name: /View and add The Salt Cartographer/ })).toBeNull())
    await expect(await body.findByText('8 titles')).toBeVisible()
  },
}

export const LibraryOnly: Story = {
  args: { items: animeResumeItems.filter((i) => i.seriesId != null) },
}

export const ExpandedModal: Story = {
  render: (args) => <AnimeResumeExpandModal opened onClose={fn()} onOpen={args.onOpen} />,
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText('9 titles')).toBeVisible()
    await expect(await body.findByRole('dialog')).toBeVisible()
  },
}

export const ExpandedModalLoading: Story = {
  parameters: { msw: [http.get(api('/home/from-anime'), () => neverResolve())] },
  render: (args) => <AnimeResumeExpandModal opened onClose={fn()} onOpen={args.onOpen} />,
}

export const ExpandedModalFailed: Story = {
  parameters: { msw: [http.get(api('/home/from-anime'), () => failWith(500))] },
  render: (args) => <AnimeResumeExpandModal opened onClose={fn()} onOpen={args.onOpen} />,
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByText(/couldn't load this rail/i)).toBeVisible()
  },
}

export const ExpandedModalEmpty: Story = {
  parameters: { msw: [http.get(api('/home/from-anime'), () => HttpResponse.json({ items: [], total: 0 }))] },
  render: (args) => <AnimeResumeExpandModal opened onClose={fn()} onOpen={args.onOpen} />,
}

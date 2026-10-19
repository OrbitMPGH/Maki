import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../../.storybook/mocks/api'
import { animeResumeItems } from '../../../.storybook/mocks/home'
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
    await expect(canvas.getByRole('link', { name: /Clockwork Tea House/ })).toHaveAttribute('href', '/series/7')
  },
}

export const OpensCatalogueEntry: Story = {
  play: async ({ args, canvas, userEvent }) => {
    // A manga that is not in the library opens the detail modal through onOpen instead of linking.
    await userEvent.click(canvas.getByRole('button', { name: /View and add The Salt Cartographer/ }))
    await expect(args.onOpen).toHaveBeenCalledWith(animeResumeItems[3].catalogue)
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
    await expect(body.getByRole('dialog')).toBeVisible()
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

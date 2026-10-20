import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { chaptersFor, filesFor, seriesMsw } from '../../.storybook/mocks/series'
import { SeriesFilesSection } from './SeriesFilesSection'

const meta = {
  component: SeriesFilesSection,
  tags: ['ai-generated'],
  args: { seriesId: 1 },
  parameters: { layout: 'padded', msw: seriesMsw() },
} satisfies Meta<typeof SeriesFilesSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Bonus booklet/)).toBeVisible()
    await expect(await canvas.findByText('Vol.3')).toBeVisible()
  },
}

export const SelectAndDelete: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Select' }))
    await userEvent.click(await canvas.findByRole('button', { name: /select all on disk/i }))
    await userEvent.click(await canvas.findByRole('button', { name: /delete selected/i }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await expect(await within(dialog).findByRole('button', { name: 'Delete' })).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Cancel' }))
  },
}

export const BinRefusesDelete: Story = {
  parameters: {
    msw: seriesMsw(
      http.delete(api('/series/:id/files'), () =>
        HttpResponse.json({ code: 'error.recycleBin.unavailable', error: 'No recycle bin', permanentDeleteAvailable: true }, { status: 409 }),
      ),
    ),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Select' }))
    await userEvent.click(await canvas.findByRole('button', { name: /select all on disk/i }))
    await userEvent.click(await canvas.findByRole('button', { name: /delete selected/i }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Delete' }))
    await expect(await within(dialog).findByRole('button', { name: 'Delete permanently' })).toBeVisible()
  },
}

export const AllLinked: Story = {
  parameters: {
    msw: seriesMsw(
      http.get(api('/series/:id/files'), () => HttpResponse.json(filesFor(1, chaptersFor(1)).filter((f) => f.status === 'linked'))),
    ),
  },
}

export const Empty: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/files'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('No files in the series folder.')).toBeVisible()
  },
}

export const Scanning: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/files'), () => neverResolve())) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Scanning folder/)).toBeVisible()
  },
}

export const Reader: Story = {
  parameters: { auth: 'reader', msw: seriesMsw() },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Bonus booklet/)).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Relink files' })).toBeNull()
  },
}

export const FolderNotFound: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/files'), () => failWith(404, 'Series folder not found'))) },
}

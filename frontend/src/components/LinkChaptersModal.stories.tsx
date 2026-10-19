import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, neverResolve } from '../../.storybook/mocks/api'
import { seriesMsw } from '../../.storybook/mocks/series'
import { LinkChaptersModal } from './LinkChaptersModal'

const meta = {
  component: LinkChaptersModal,
  tags: ['ai-generated'],
  args: { seriesId: 1, chapterIds: [1170, 1180], opened: true, onClose: fn() },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof LinkChaptersModal>

export default meta
type Story = StoryObj<typeof meta>

const dialogOf = (canvasElement: HTMLElement) => within(canvasElement.ownerDocument.body).findByRole('dialog')

export const PickAFile: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(dialog).toHaveTextContent('Link 2 chapters to a file')
    await expect(await within(dialog).findByText(/Bonus booklet/)).toBeVisible()
  },
}

export const LinkSelectedFile: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const dialog = await dialogOf(canvasElement)
    await expect(within(dialog).getByRole('button', { name: 'Link' })).toBeDisabled()
    await userEvent.click(await within(dialog).findByRole('button', { name: /Bonus booklet/ }))
    await userEvent.click(within(dialog).getByRole('button', { name: 'Link' }))
    await waitFor(() => expect(args.onClose).toHaveBeenCalled())
  },
}

export const SingleChapter: Story = {
  args: { chapterIds: [1170] },
  play: async ({ canvasElement }) => {
    await expect(await dialogOf(canvasElement)).toHaveTextContent('Link 1 chapter to a file')
  },
}

export const NoFiles: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/files'), () => HttpResponse.json([]))) },
  play: async ({ canvasElement }) => {
    await expect(await within(await dialogOf(canvasElement)).findByText('No files found in the series folder.')).toBeVisible()
  },
}

export const LoadingFiles: Story = {
  parameters: { msw: seriesMsw(http.get(api('/series/:id/files'), () => neverResolve())) },
}

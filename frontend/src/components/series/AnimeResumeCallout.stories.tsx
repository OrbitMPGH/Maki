import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { animeResume } from '../../../.storybook/mocks/series'
import { AnimeResumeCallout } from './AnimeResumeCallout'

const meta = {
  component: AnimeResumeCallout,
  tags: ['ai-generated'],
  args: {
    resume: animeResume,
    variant: 'library',
    onMarkWatched: fn(),
    onRead: fn(),
    onDismiss: fn(),
  },
} satisfies Meta<typeof AnimeResumeCallout>

export default meta
type Story = StoryObj<typeof meta>

export const Library: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: /read ch\. 39/i }))
    await expect(args.onRead).toHaveBeenCalledOnce()
    await userEvent.click(canvas.getByRole('button', { name: /not this anime/i }))
    await expect(args.onDismiss).toHaveBeenCalledOnce()
  },
}

export const MarkWatchedAsksFirst: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: /mark ch\. 1 to 38 watched/i }))
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: /mark chapters watched/i })
    await userEvent.click(within(dialog).getByRole('button', { name: 'Mark watched' }))
    await expect(args.onMarkWatched).toHaveBeenCalledWith(38)
  },
}

export const SeasonBasis: Story = {
  args: {
    resume: { ...animeResume, basis: 'seasonCount', coveredLabel: 'season 1', nextLabel: 'season 2', nextFrom: 39, nextTo: 76 },
  },
}

export const AlreadyMarked: Story = {
  args: { resume: { ...animeResume, unmarkedCount: 0 } },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('button', { name: /mark ch\./i })).toBeNull()
  },
}

export const ActionRunning: Story = {
  args: { pending: 'read' },
}

export const Catalogue: Story = {
  args: { variant: 'catalogue', inLibrarySeriesId: 1 },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('link', { name: /open series/i })).toHaveAttribute('href', '/series/1')
  },
}

export const CatalogueNotInLibrary: Story = {
  args: { variant: 'catalogue', inLibrarySeriesId: null },
}

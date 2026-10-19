import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { ImportMatchFinder } from './ImportMatchFinder'

const meta = {
  component: ImportMatchFinder,
  tags: ['ai-generated'],
  args: {
    folderName: 'zz_unsorted_scans_2019',
    initialQuery: 'Paper Crane',
    onPick: fn(),
    onClose: fn(),
  },
} satisfies Meta<typeof ImportMatchFinder>

export default meta
type Story = StoryObj<typeof meta>

export const SearchResults: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: /Find a match for/ })
    const option = await within(dialog).findByRole('button', { name: 'Use Paper Crane Cartel' }, { timeout: 4000 })
    await userEvent.click(option)
    await expect(args.onPick).toHaveBeenCalledWith(expect.objectContaining({ providerId: 'mb-30020' }))
  },
}

export const NothingFound: Story = {
  args: { initialQuery: 'zz nothing like this' },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    await waitFor(async () => expect(await body.findByText('Nothing found for that title.', undefined, { timeout: 4000 })).toBeVisible())
  },
}

export const PasteALink: Story = {
  args: { initialQuery: '' },
  play: async ({ args, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: /Find a match for/ })
    await userEvent.type(within(dialog).getByLabelText(/MangaBaka id, or a MangaBaka/), 'https://anilist.co/manga/30013')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Use' }))
    await waitFor(() => expect(args.onPick).toHaveBeenCalled())
    await expect(args.onPick.mock.calls[0][0]).toMatchObject({ title: 'Paper Crane Cartel' })
  },
}

export const PastedLinkNotFound: Story = {
  args: { initialQuery: '' },
  play: async ({ canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: /Find a match for/ })
    await userEvent.type(within(dialog).getByLabelText(/MangaBaka id, or a MangaBaka/), 'nope-999')
    await userEvent.click(within(dialog).getByRole('button', { name: 'Use' }))
    await waitFor(async () => expect(await within(dialog).findByText('No series matches that id or link.')).toBeVisible())
  },
}

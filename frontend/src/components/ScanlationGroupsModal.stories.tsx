import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../.storybook/mocks/api'
import { GlobalScanlationGroupsModal, SeriesScanlationGroupsModal } from './ScanlationGroupsModal'
import type { GlobalScanlationGroups, ScanlationGroupLists, SeriesScanlationGroups } from '../api/scanlationGroups'

const globalGroups = {
  preferred: ['Tidewater Scans'],
  blocked: ['Rushjob TL'],
  seen: [
    { name: 'Tidewater Scans', chapters: 412 },
    { name: 'Night Owl TL', chapters: 188 },
    { name: 'Rushjob TL', chapters: 61 },
    { name: 'Paper Lantern', chapters: 23 },
  ],
} satisfies GlobalScanlationGroups

const seriesGroups = {
  preferred: [],
  blocked: [],
  global: { preferred: ['Tidewater Scans'], blocked: ['Rushjob TL'] },
  seen: [
    { name: 'Night Owl TL', chapters: 54 },
    { name: 'Tidewater Scans', chapters: 40 },
    { name: 'Paper Lantern', chapters: 6 },
  ],
  sources: ['MangaDex'],
  unreachable: [],
} satisfies SeriesScanlationGroups

const saved = fn()

/** Mantine labels the suggestion list after its input too, so take the input. */
const inputLabelled = async (label: string) =>
  (await within(document.body).findAllByLabelText(label)).find((e) => e.tagName === 'INPUT')!

const meta = {
  component: GlobalScanlationGroupsModal,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn() },
  parameters: {
    msw: [
      http.get(api('/scanlationgroups'), () => HttpResponse.json(globalGroups)),
      http.put(api('/scanlationgroups'), async ({ request }) => {
        const body = (await request.json()) as ScanlationGroupLists
        saved(body)
        return HttpResponse.json(body)
      }),
    ],
  },
} satisfies Meta<typeof GlobalScanlationGroupsModal>

export default meta
type Story = StoryObj<typeof meta>

export const Global: Story = {
  play: async () => {
    const dialog = within(document.body)
    await expect(await dialog.findByText('Tidewater Scans')).toBeInTheDocument()
    await expect(await dialog.findByText('Rushjob TL')).toBeInTheDocument()
  },
}

export const AddAndReorder: Story = {
  play: async ({ userEvent }) => {
    saved.mockClear()
    const dialog = within(document.body)
    const add = await inputLabelled('Add a preferred group')
    await userEvent.type(add, 'Night Owl TL{Enter}')
    await userEvent.click(await dialog.findByRole('button', { name: 'Move Night Owl TL up' }))
    await userEvent.click(dialog.getByRole('button', { name: 'Save' }))
    await waitFor(() =>
      expect(saved).toHaveBeenCalledWith({ preferred: ['Night Owl TL', 'Tidewater Scans'], blocked: ['Rushjob TL'] }),
    )
  },
}

export const Series: StoryObj<typeof SeriesScanlationGroupsModal> = {
  render: (args) => <SeriesScanlationGroupsModal {...args} />,
  args: { seriesId: 7, opened: true, onClose: fn(), onSaved: fn() },
  parameters: {
    msw: [
      http.get(api('/scanlationgroups/series/7'), () => HttpResponse.json(seriesGroups)),
      http.put(api('/scanlationgroups/series/7'), async ({ request }) => HttpResponse.json(await request.json())),
    ],
  },
  play: async ({ args, userEvent }) => {
    const dialog = within(document.body)
    await expect(await dialog.findByText(/the global order applies: Tidewater Scans/)).toBeInTheDocument()
    await expect(dialog.getByText(/Also blocked everywhere: Rushjob TL/)).toBeInTheDocument()
    await userEvent.type(await inputLabelled('Block a group'), 'Paper Lantern{Enter}')
    await userEvent.click(dialog.getByRole('button', { name: 'Save' }))
    await waitFor(() => expect(args.onSaved).toHaveBeenCalled())
  },
}

export const SeriesSourceUnreachable: StoryObj<typeof SeriesScanlationGroupsModal> = {
  render: (args) => <SeriesScanlationGroupsModal {...args} />,
  args: { seriesId: 8, opened: true, onClose: fn() },
  parameters: {
    msw: [
      http.get(api('/scanlationgroups/series/8'), () =>
        HttpResponse.json({ ...seriesGroups, seen: [], unreachable: ['MangaDex'] } satisfies SeriesScanlationGroups)),
    ],
  },
  play: async () => {
    const dialog = within(document.body)
    await expect(await dialog.findByText(/Could not read the groups on MangaDex/)).toBeInTheDocument()
  },
}

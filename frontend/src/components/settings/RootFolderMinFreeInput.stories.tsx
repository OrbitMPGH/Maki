import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { RootFolderMinFreeInput } from './RootFolderMinFreeInput'

const GB = 1024 ** 3
const folder = {
  id: 3,
  path: '/mnt/ssd/manga',
  freeSpace: 42 * GB,
  accessible: true,
  minFreeBytes: null,
  defaultMinFreeBytes: 5 * GB,
}

const meta = {
  component: RootFolderMinFreeInput,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: { folder },
} satisfies Meta<typeof RootFolderMinFreeInput>

export default meta
type Story = StoryObj<typeof meta>

const onSave = fn()

export const DefaultThreshold: Story = {
  play: async ({ canvas }) => {
    const input = await canvas.findByRole('textbox', { name: 'Free space to keep on /mnt/ssd/manga' })
    await waitFor(() => expect(input).toHaveValue('5 GB'))
  },
}

export const SavesOnBlur: Story = {
  parameters: {
    msw: [
      http.put(api('/rootfolder/:id/minfree'), async ({ params, request }) => {
        onSave(Number(params.id), await request.json())
        return HttpResponse.json({ ...folder, minFreeBytes: 20 * GB })
      }),
    ],
  },
  beforeEach: () => onSave.mockClear(),
  play: async ({ canvas, userEvent }) => {
    const input = await canvas.findByRole('textbox', { name: 'Free space to keep on /mnt/ssd/manga' })
    await userEvent.clear(input)
    await userEvent.type(input, '20')
    await userEvent.tab()
    await waitFor(() => expect(onSave).toHaveBeenCalledWith(3, { minFreeBytes: 20 * GB }))
  },
}

export const ZeroNeverHolds: Story = {
  args: { folder: { ...folder, minFreeBytes: 0 } },
  play: async ({ canvas }) => {
    const input = await canvas.findByRole('textbox', { name: 'Free space to keep on /mnt/ssd/manga' })
    await waitFor(() => expect(input).toHaveValue('0 GB'))
  },
}

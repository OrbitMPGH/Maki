import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import { seriesMsw } from '../../.storybook/mocks/series'
import { SeriesTagsEditor } from './SeriesTagsEditor'

const meta = {
  component: SeriesTagsEditor,
  tags: ['ai-generated'],
  args: { seriesId: 1, tagIds: [1, 2] },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof SeriesTagsEditor>

export default meta
type Story = StoryObj<typeof meta>

export const WithTags: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Favourites')).toBeVisible()
    await expect(await canvas.findByText('Reading now')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Edit' })).toBeVisible()
  },
}

export const NoTags: Story = {
  args: { tagIds: [] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: '+ Add tags' })).toBeVisible()
  },
}

export const Editing: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Edit' }))
    await expect(await canvas.findByText('Press Enter to create a new tag')).toBeVisible()
    await waitFor(() => expect(canvas.getByLabelText('Your tags')).toHaveFocus())
  },
}

export const CreatesNewTag: Story = {
  parameters: {
    msw: seriesMsw(
      http.put(api('/series/:id/tags'), () => HttpResponse.json({ tagIds: [1, 2, 99] })),
    ),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Edit' }))
    await userEvent.type(await canvas.findByLabelText('Your tags'), 'Needs a re-read{Enter}')
    // The input disables while saving, which blurs it and drops back to the chip view.
    await waitFor(() => expect(canvas.getByRole('heading', { name: 'Your tags' })).toBeVisible())
  },
}

export const SaveFails: Story = {
  parameters: {
    msw: seriesMsw(http.put(api('/series/:id/tags'), () => failWith(500, 'Could not save'))),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Edit' }))
    await userEvent.type(await canvas.findByLabelText('Your tags'), 'Weekend binge{Enter}')
  },
}

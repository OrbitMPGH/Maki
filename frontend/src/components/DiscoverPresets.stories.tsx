import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import { discoverPage } from '../../.storybook/mocks/discover'
import { HiddenContentButton, PresetMenu } from './DiscoverPresets'

const meta = {
  component: PresetMenu,
  tags: ['ai-generated'],
  args: { current: () => ({ types: ['manhwa'], minRating: 70 }), onLoad: fn() },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof PresetMenu>

export default meta
type Story = StoryObj<typeof meta>

const body = (el: HTMLElement) => within(el.ownerDocument.body)

export const Closed: Story = {}

export const MenuOpen: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Saved filters' }))
    await expect(await body(canvasElement).findByText('Short and finished')).toBeInTheDocument()
    await expect(await body(canvasElement).findByText('Cozy slice of life')).toBeInTheDocument()
  },
}

export const LoadPreset: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Saved filters' }))
    await userEvent.click(await body(canvasElement).findByText('Short and finished'))
    await waitFor(() => expect(args.onLoad).toHaveBeenCalledWith(expect.objectContaining({ statuses: ['completed'], maxChapters: 60 })))
  },
}

export const NothingSaved: Story = {
  parameters: { msw: discoverPage(http.get(api('/discover/filters'), () => HttpResponse.json([]))) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Saved filters' }))
    await expect(await body(canvasElement).findByText('Nothing saved yet.')).toBeInTheDocument()
  },
}

export const SaveCurrentFilters: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Saved filters' }))
    await userEvent.click(await body(canvasElement).findByText('Save these filters…'))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Save filters' })
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Save' }))
    await expect(await within(dialog).findByText('Give it a name')).toBeVisible()
    await userEvent.type(await within(dialog).findByLabelText('Name'), 'Long running manhwa')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Save' }))
    await expect(await body(canvasElement).findByText('Filter saved')).toBeVisible()
  },
}

export const SaveFails: Story = {
  parameters: { msw: discoverPage(http.post(api('/discover/filters'), () => failWith(409, 'A filter with that name exists'))) },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Saved filters' }))
    await userEvent.click(await body(canvasElement).findByText('Save these filters…'))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Save filters' })
    await userEvent.type(await within(dialog).findByLabelText('Name'), 'Duplicate')
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Save' }))
    await expect(await within(dialog).findByText('A filter with that name exists')).toBeVisible()
  },
}

export const DeleteWithUndo: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Saved filters' }))
    const [remove] = await body(canvasElement).findAllByRole('button', { name: 'Delete saved filter' })
    await userEvent.click(remove)
    await expect(await body(canvasElement).findByText('Deleted saved filter Short and finished.')).toBeVisible()
    await expect(await body(canvasElement).findByRole('button', { name: 'Undo' })).toBeVisible()
  },
}

export const HiddenButton: StoryObj = {
  render: () => <HiddenContentButton />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Hidden everywhere (2)' })).toBeVisible()
  },
}

export const HiddenButtonEmptyList: StoryObj = {
  render: () => <HiddenContentButton />,
  parameters: {
    msw: discoverPage(http.get(api('/recommendations/discover/hidden'), () => HttpResponse.json({ terms: [] }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Hidden everywhere' })).toBeVisible()
  },
}

export const HiddenListModal: StoryObj = {
  render: () => <HiddenContentButton />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Hidden everywhere/ }))
    const dialog = await body(canvasElement).findByRole('dialog', { name: 'Hidden everywhere' })
    await expect(await within(dialog).findByText('Gore')).toBeVisible()
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Save' }))
    await expect(await body(canvasElement).findByText('Hidden list saved')).toBeVisible()
  },
}

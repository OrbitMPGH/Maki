import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { useUiSettings } from '../../api/hooks'
import { cover } from '../../../.storybook/mocks/api'
import { customRails, homePage, homeUi } from '../../../.storybook/mocks/home'
import { HOME_LAYOUT_CONFIG, HOME_SECTION_DEFS } from '../home/homeSectionDefs'
import { PageLayoutEditor, PageLayoutEditorLoading } from './PageLayoutEditor'

const layout = homeUi({ rails: [1, 2, 3], off: ['following'], hero: { jumpback: true } }).homeLayout.sections

const meta = {
  component: PageLayoutEditor,
  tags: ['ai-generated'],
  args: {
    page: 'home',
    placement: 'home',
    registry: HOME_SECTION_DEFS,
    config: HOME_LAYOUT_CONFIG,
    initial: layout,
    rails: customRails,
    railLimit: 20,
    onExit: fn(),
  },
  parameters: { layout: 'padded', msw: homePage() },
  // Saving reads the settings query out of the cache, which the Home page fills before it mounts the editor.
  render: function Render(args) {
    const { data } = useUiSettings()
    return data ? <PageLayoutEditor {...args} /> : <></>
  },
} satisfies Meta<typeof PageLayoutEditor>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Editing layout')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: /Move At a glance/ })).toBeVisible()
    await expect(await canvas.findByText('Unread favourites')).toBeVisible()
    await expect(await canvas.findByText('Hidden')).toBeVisible()
  },
}

export const OpenSectionProperties: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByText('Continue reading'))
    const body = within(canvasElement.ownerDocument.body)
    const drawer = await body.findByRole('dialog', { name: 'Continue reading' })
    await waitFor(() => expect(within(drawer).getByRole('switch', { name: 'Show this section' })).toBeChecked())
  },
}

export const KeyboardReorder: Story = {
  play: async ({ canvas, userEvent }) => {
    const grip = await canvas.findByRole('button', { name: 'Move At a glance' })
    grip.focus()
    await userEvent.keyboard('{ArrowDown}')
    await waitFor(() => expect(canvas.getByText('At a glance moved to position 2 of 12')).toBeInTheDocument())
  },
}

export const ChangeThenDone: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByText('Currently popular'))
    const body = within(canvasElement.ownerDocument.body)
    const drawer = await body.findByRole('dialog', { name: 'Currently popular' })
    await userEvent.click(await within(drawer).findByRole('switch', { name: 'Show this section' }))
    await userEvent.keyboard('{Escape}')
    await userEvent.click(await canvas.findByRole('button', { name: 'Done' }))
    await waitFor(() => expect(args.onExit).toHaveBeenCalledOnce())
    await expect(await body.findByText('Layout saved')).toBeVisible()
  },
}

export const DoneWithoutChanges: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Done' }))
    await expect(args.onExit).toHaveBeenCalledOnce()
  },
}

export const Cancel: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Cancel' }))
    await expect(args.onExit).toHaveBeenCalledOnce()
  },
}

export const AddRailOpensEditor: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Add a rail' }))[0])
    await waitFor(async () => expect(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'New rail' })).toBeVisible())
  },
}

export const UnavailableSections: Story = {
  args: {
    unavailable: (key) =>
      key === 'recommended' || key === 'popular' ? { id: 'story.unavailable', message: 'Needs the local MangaBaka database' } : null,
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByText('Needs the local MangaBaka database')).toHaveLength(2))
  },
}

export const WithSummariesAndPreviews: Story = {
  args: {
    summary: (key) => (key === 'recent' ? '7 series with new chapters' : null),
    preview: (key) => (key === 'recent' ? [1, 5, 9, 3, 10].map((id) => cover(id)) : []),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('7 series with new chapters')).toBeVisible()
  },
}

export const WithoutRails: Story = {
  args: { initial: homeUi().homeLayout.sections, rails: [] },
}

export const Loading: Story = {
  render: () => <PageLayoutEditorLoading onExit={fn()} />,
}

export const LoadFailed: Story = {
  render: (args) => <PageLayoutEditorLoading failed onExit={args.onExit} />,
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('The layout could not be loaded.')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Exit editing' }))
    await expect(args.onExit).toHaveBeenCalledOnce()
  },
}

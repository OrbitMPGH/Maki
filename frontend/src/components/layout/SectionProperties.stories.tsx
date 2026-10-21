import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { customRails, homePage } from '../../../.storybook/mocks/home'
import { HOME_SECTION_DEFS } from '../home/homeSectionDefs'
import type { SectionDef } from './pageLayout'
import { SectionProperties } from './SectionProperties'

const meta = {
  component: SectionProperties,
  tags: ['ai-generated'],
  args: {
    section: { key: 'continue', enabled: true, hero: true, panels: null },
    index: 2,
    total: 9,
    label: 'Continue reading',
    def: HOME_SECTION_DEFS.continue,
    rail: undefined,
    onChange: fn(),
    onMove: fn(),
    onClose: fn(),
  },
  parameters: { msw: homePage() },
} satisfies Meta<typeof SectionProperties>

export default meta
type Story = StoryObj<typeof meta>

const drawer = (canvasElement: HTMLElement) =>
  within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Continue reading' })

export const HeroSection: Story = {
  play: async ({ canvasElement }) => {
    const panel = await drawer(canvasElement)
    await waitFor(() => expect(within(panel).getByRole('switch', { name: /Lead with a hero/ })).toBeChecked())
    await expect(await within(panel).findByText('Series with a chapter part-way through.')).toBeVisible()
  },
}

export const ToggleHeroAndVisibility: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const panel = await drawer(canvasElement)
    await userEvent.click(await within(panel).findByRole('switch', { name: /Lead with a hero/ }))
    await waitFor(() => expect(args.onChange).toHaveBeenCalledWith({ hero: false }))
    await userEvent.click(await within(panel).findByRole('switch', { name: 'Show this section' }))
    await waitFor(() => expect(args.onChange).toHaveBeenCalledWith({ enabled: false }))
  },
}

export const Moving: Story = {
  play: async ({ args, canvasElement, userEvent }) => {
    const panel = await drawer(canvasElement)
    await userEvent.click(await within(panel).findByRole('button', { name: 'Top' }))
    await waitFor(() => expect(args.onMove).toHaveBeenCalledWith(0))
    await userEvent.click(await within(panel).findByRole('button', { name: 'Down' }))
    await waitFor(() => expect(args.onMove).toHaveBeenCalledWith(3))
    await userEvent.click(await within(panel).findByRole('button', { name: 'Bottom' }))
    await waitFor(() => expect(args.onMove).toHaveBeenCalledWith(8))
  },
}

export const FirstSection: Story = {
  args: { index: 0 },
  play: async ({ canvasElement }) => {
    const panel = await drawer(canvasElement)
    await waitFor(() => expect(within(panel).getByRole('button', { name: 'Top' })).toBeDisabled())
    await waitFor(() => expect(within(panel).getByRole('button', { name: 'Up' })).toBeDisabled())
    await waitFor(() => expect(within(panel).getByRole('button', { name: 'Down' })).toBeEnabled())
  },
}

export const LastSection: Story = {
  args: { index: 8 },
  play: async ({ canvasElement }) => {
    const panel = await drawer(canvasElement)
    await waitFor(() => expect(within(panel).getByRole('button', { name: 'Bottom' })).toBeDisabled())
    await waitFor(() => expect(within(panel).getByRole('button', { name: 'Down' })).toBeDisabled())
  },
}

export const HiddenSection: Story = {
  args: { section: { key: 'continue', enabled: false, hero: false, panels: null } },
  play: async ({ canvasElement }) => {
    const panel = await drawer(canvasElement)
    await waitFor(() => expect(within(panel).getByRole('switch', { name: 'Show this section' })).not.toBeChecked())
  },
}

// No shipping section has panels any more; the editor still supports them, so these use a made-up one.
const PANEL_KEYS = ['stats', 'progress', 'toread']
const panelledDef: SectionDef = {
  ...HOME_SECTION_DEFS.glance,
  panels: [
    { key: 'stats', label: { id: 'story.stats', message: 'Library counts' } },
    { key: 'progress', label: { id: 'story.progress', message: 'Your progress' } },
    { key: 'toread', label: { id: 'story.toread', message: 'Waiting to read' } },
  ],
}

export const Panels: Story = {
  args: {
    section: {
      key: 'glance',
      enabled: true,
      hero: null,
      panels: PANEL_KEYS.map((key) => ({ key, enabled: key !== 'toread' })),
    },
    label: 'At a glance',
    def: panelledDef,
    panelHint: (_section, panel) => (panel === 'toread' ? { id: 'story.hint', message: 'Needs read tracking: use the reader or connect Kavita.' } : null),
  },
  play: async ({ args, canvasElement, userEvent }) => {
    const panel = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'At a glance' })
    await expect(await within(panel).findByText(/Needs read tracking/)).toBeVisible()
    await userEvent.click(await within(panel).findByRole('switch', { name: 'Library counts' }))
    await waitFor(() => expect(args.onChange).toHaveBeenCalledWith({
      panels: [
        { key: 'stats', enabled: false },
        { key: 'progress', enabled: true },
        { key: 'toread', enabled: false },
      ],
    }))
  },
}

export const AllPanelsOff: Story = {
  args: {
    section: { key: 'glance', enabled: true, hero: null, panels: PANEL_KEYS.map((key) => ({ key, enabled: false })) },
    label: 'At a glance',
    def: panelledDef,
  },
  play: async ({ canvasElement }) => {
    const panel = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'At a glance' })
    await expect(await within(panel).findByText('With every panel off, this section stays empty.')).toBeVisible()
  },
}

export const CustomRailSection: Story = {
  args: {
    section: { key: 'rail:3', enabled: true, hero: null, panels: null },
    label: 'Top rated romance',
    def: undefined,
    rail: customRails[2],
  },
  play: async ({ canvasElement, userEvent }) => {
    const panel = await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Top rated romance' })
    await expect(await within(panel).findByText('Rail changes save straight away. Done saves the layout.')).toBeVisible()
    await waitFor(() => expect(within(panel).getByRole('textbox', { name: 'Name' })).toHaveValue('Top rated romance'))
    await userEvent.click(await within(panel).findByRole('button', { name: 'Delete rail' }))
    await waitFor(async () => expect(await within(canvasElement.ownerDocument.body).findByRole('dialog', { name: 'Delete rail' })).toBeVisible())
  },
}

export const Closed: Story = {
  args: { section: null, index: -1, label: '', def: undefined },
  play: async ({ canvasElement }) => {
    await expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).toBeNull()
  },
}

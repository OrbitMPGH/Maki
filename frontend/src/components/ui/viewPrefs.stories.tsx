import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { Group, Menu } from '@mantine/core'
import {
  DensityControl,
  DensityMenuItems,
  ToolbarMenuButton,
  ViewModeToggle,
  ViewPrefsControls,
  useViewPrefs,
  type Density,
  type ViewMode,
} from './viewPrefs'

const meta = {
  component: ViewModeToggle,
  tags: ['ai-generated'],
  args: { value: 'grid', onChange: fn() },
  render: function Render(args) {
    const [value, setValue] = useState<ViewMode>(args.value)
    return (
      <ViewModeToggle
        {...args}
        value={value}
        onChange={(mode) => {
          setValue(mode)
          args.onChange(mode)
        }}
      />
    )
  },
} satisfies Meta<typeof ViewModeToggle>

export default meta
type Story = StoryObj<typeof meta>

export const GridToList: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(canvas.getByRole('radio', { name: 'Grid view' })).toBeChecked()
    await userEvent.click(canvas.getByRole('radio', { name: 'List view' }))
    await expect(args.onChange).toHaveBeenCalledWith('list')
    await expect(canvas.getByRole('radio', { name: 'List view' })).toBeChecked()
  },
}

export const ListSelected: Story = {
  args: { value: 'list' },
}

export const DensitySegments: Story = {
  render: function Render() {
    const [value, setValue] = useState<Density>('default')
    return <DensityControl value={value} onChange={setValue} />
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByText('Comfortable'))
    await expect(canvas.getByRole('radio', { name: 'Comfortable' })).toBeChecked()
  },
}

export const OverflowMenu: Story = {
  render: function Render() {
    const [density, setDensity] = useState<Density>('compact')
    return (
      <Menu withinPortal>
        <Menu.Target>
          <ToolbarMenuButton />
        </Menu.Target>
        <Menu.Dropdown>
          <DensityMenuItems value={density} onChange={setDensity} />
        </Menu.Dropdown>
      </Menu>
    )
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: 'More options' }))
    const menu = within(canvasElement.ownerDocument.body)
    await expect(await menu.findByRole('menuitem', { name: 'Compact' })).toHaveAttribute('aria-current', 'true')
    await userEvent.click(menu.getByRole('menuitem', { name: 'Comfortable' }))
    await userEvent.click(canvas.getByRole('button', { name: 'More options' }))
    await expect(await menu.findByRole('menuitem', { name: 'Comfortable' })).toHaveAttribute('aria-current', 'true')
  },
}

export const WithStoredPrefs: Story = {
  beforeEach: () => {
    try {
      localStorage.removeItem('story-view')
      localStorage.removeItem('story-density')
    } catch {
      // Storage blocked: the controls start from their defaults anyway.
    }
  },
  render: function Render() {
    const prefs = useViewPrefs('story')
    return (
      <Group>
        <ViewPrefsControls prefs={prefs} />
        <span>
          {prefs.viewMode} / {prefs.density}
        </span>
      </Group>
    )
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('radio', { name: 'List view' }))
    await expect(canvas.getByText(/^list \//)).toBeVisible()
  },
}

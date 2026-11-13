import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Menu } from '@mantine/core'
import { OverflowButton } from './OverflowButton'

const meta = {
  component: OverflowButton,
  tags: ['ai-generated'],
} satisfies Meta<typeof OverflowButton>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'More actions' })).toBeVisible()
  },
}

export const NamedForARow: Story = {
  args: { 'aria-label': 'Actions for Chapter 12' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Actions for Chapter 12' })).toBeVisible()
  },
}

export const MenuTarget: Story = {
  render: () => (
    <Menu withinPortal={false}>
      <Menu.Target>
        <OverflowButton />
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item>Edit</Menu.Item>
      </Menu.Dropdown>
    </Menu>
  ),
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'More actions' }))
    await expect(await canvas.findByRole('menuitem', { name: 'Edit' })).toBeVisible()
  },
}

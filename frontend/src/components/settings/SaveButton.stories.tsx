import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { SaveButton } from './SaveButton'

const meta = {
  component: SaveButton,
  tags: ['ai-generated'],
  args: { onClick: fn() },
} satisfies Meta<typeof SaveButton>

export default meta
type Story = StoryObj<typeof meta>

export const Clean: Story = {
  args: { dirty: false },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Save' })).toBeDisabled()
    await expect(canvas.queryByText('Unsaved changes')).toBeNull()
  },
}

export const Dirty: Story = {
  args: { dirty: true },
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('Unsaved changes')).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Save' }))
    await expect(args.onClick).toHaveBeenCalledOnce()
  },
}

export const Saving: Story = {
  args: { dirty: true, loading: true },
}

export const DirtyButBlocked: Story = {
  args: { dirty: true, disabled: true },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Save' })).toBeDisabled()
  },
}

export const UnknownDirtyState: Story = {
  args: {},
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Save' })).toBeEnabled()
  },
}

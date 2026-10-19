import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { SelectCards } from './SelectCards'

const options = [
  { value: 'off', title: 'Off', subtitle: 'Titles only' },
  { value: 'base', title: 'Base', subtitle: 'About 240 MB of RAM' },
  { value: 'large', title: 'Large', subtitle: 'Better matches, slower' },
]

const meta = {
  component: SelectCards<string>,
  tags: ['ai-generated'],
  args: { options, value: 'base', onChange: () => {}, fillLeft: false },
  render: function Render(args) {
    const [value, setValue] = useState(args.value)
    return <SelectCards {...args} value={value} onChange={setValue} />
  },
} satisfies Meta<typeof SelectCards<string>>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('button', { name: /Base/ })).toHaveAttribute('aria-pressed', 'true')
    await userEvent.click(canvas.getByRole('button', { name: /Large/ }))
    await expect(canvas.getByRole('button', { name: /Large/ })).toHaveAttribute('aria-pressed', 'true')
    await expect(canvas.getByRole('button', { name: /Base/ })).toHaveAttribute('aria-pressed', 'false')
  },
}

export const FillLeft: Story = {
  args: { fillLeft: true, value: 'large' },
}

export const Disabled: Story = {
  args: { disabled: true },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: /Off/ })).toBeDisabled()
  },
}

import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { SettingsNumberInput } from './SettingsNumberInput'

const meta = {
  component: SettingsNumberInput,
  tags: ['ai-generated'],
  args: { value: 5, onChange: fn(), label: 'Retry a failed download up to (attempts)', min: 1, max: 20, w: 280 },
  render: function Render(args) {
    const [value, setValue] = useState(args.value)
    return (
      <SettingsNumberInput
        {...args}
        value={value}
        onChange={(next) => {
          setValue(next)
          args.onChange(next)
        }}
      />
    )
  },
} satisfies Meta<typeof SettingsNumberInput>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const ClampsOnBlur: Story = {
  play: async ({ args, canvas, userEvent }) => {
    const field = await canvas.findByRole('textbox')
    await userEvent.clear(field)
    await userEvent.type(field, '99')
    // 99 is out of range, so the parent is not told until blur settles it to the maximum.
    await waitFor(() => expect(args.onChange).not.toHaveBeenCalledWith(99))
    await userEvent.tab()
    await waitFor(() => expect(args.onChange).toHaveBeenLastCalledWith(20))
    await waitFor(() => expect(field).toHaveValue('20'))
  },
}

export const EmptySnapsBack: Story = {
  play: async ({ canvas, userEvent }) => {
    const field = await canvas.findByRole('textbox')
    await userEvent.clear(field)
    await userEvent.tab()
    await waitFor(() => expect(field).toHaveValue('5'))
  },
}

export const ZeroMeansOff: Story = {
  args: { value: 0, min: 0, label: 'Download timeout (minutes)', description: '0 means no limit.' },
}

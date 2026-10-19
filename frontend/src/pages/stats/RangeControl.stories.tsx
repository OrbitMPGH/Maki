import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { RangeControl } from './RangeControl'
import type { RangePreset } from './StatsRange'

const meta = {
  component: RangeControl,
  tags: ['ai-generated'],
  args: {
    preset: '30d',
    year: 2026,
    month: null,
    yearOptions: ['2024', '2025', '2026'],
    size: 'sm',
    onPresetChange: () => {},
    onYearChange: () => {},
    onMonthChange: () => {},
  },
  // Controlled component: hold the window here so the story behaves like the Stats page.
  render: function Render(args) {
    const [preset, setPreset] = useState<RangePreset>(args.preset)
    const [year, setYear] = useState(args.year)
    const [month, setMonth] = useState<number | null>(args.month)
    return (
      <RangeControl
        {...args}
        preset={preset}
        onPresetChange={setPreset}
        year={year}
        onYearChange={setYear}
        month={month}
        onMonthChange={setMonth}
      />
    )
  },
} satisfies Meta<typeof RangeControl>

export default meta
type Story = StoryObj<typeof meta>

export const Presets: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByText('90 days'))
    await expect(canvas.getByRole('radio', { name: '90 days' })).toBeChecked()
    await expect(canvas.queryByRole('combobox', { name: 'Year' })).toBeNull()
  },
}

export const YearDrillDown: Story = {
  args: { preset: 'year' },
  play: async ({ canvas, userEvent, canvasElement }) => {
    await expect(canvas.getByRole('combobox', { name: 'Year' })).toHaveValue('2026')
    await userEvent.click(canvas.getByRole('combobox', { name: 'Month' }))
    // The month list renders in a portal.
    await userEvent.click(await within(canvasElement.ownerDocument.body).findByRole('option', { name: 'March' }))
    await expect(canvas.getByRole('combobox', { name: 'Month' })).toHaveValue('March')
  },
}

export const Compact: Story = {
  args: { size: 'xs', preset: 'all' },
}

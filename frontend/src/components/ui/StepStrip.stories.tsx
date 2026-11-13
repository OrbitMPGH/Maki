import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent } from 'storybook/test'
import { StepStrip } from './StepStrip'

const meta = {
  component: StepStrip,
  tags: ['ai-generated'],
  args: {
    label: 'Import progress',
    steps: [
      { label: 'Scan', hint: '/library/manga', state: 'done' },
      { label: 'Review', hint: '3 folders selected', state: 'current' },
      { label: 'Import', hint: 'Link files and report results', state: 'todo' },
    ],
  },
} satisfies Meta<typeof StepStrip>

export default meta
type Story = StoryObj<typeof meta>

export const Strip: Story = {
  play: async ({ canvas }) => {
    const items = await canvas.findAllByRole('listitem')
    await expect(items).toHaveLength(3)
    await expect(items[1].querySelector('[aria-current="step"]')).not.toBeNull()
  },
}

export const Clickable: Story = {
  args: {
    onSelect: fn(),
    steps: [
      { label: 'Site', state: 'done' },
      { label: 'Selectors', state: 'current' },
      { label: 'Details', state: 'todo', disabled: true },
    ],
  },
  play: async ({ canvas, args }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /Site/ }))
    await expect(args.onSelect).toHaveBeenCalledWith(0)
    await expect(await canvas.findByRole('button', { name: /Details/ })).toBeDisabled()
  },
}

export const Rail: Story = {
  args: {
    layout: 'rail',
    label: 'Setup steps',
    steps: [
      { label: 'Welcome', state: 'done' },
      { label: 'Library', hint: 'No folder yet', tone: 'warn', state: 'current' },
      { label: 'Discovery', state: 'todo' },
    ],
  },
}

export const Five: Story = {
  args: {
    steps: ['Source', 'Format', 'Options', 'Estimate', 'Review'].map((label, i) => ({
      label,
      state: i < 2 ? 'done' : i === 2 ? 'current' : 'todo',
    })),
  },
}

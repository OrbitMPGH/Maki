import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { Badge } from '@mantine/core'
import { expect } from 'storybook/test'
import { PriorityList } from './PriorityList'

const names: Record<string, string> = {
  mangadex: 'MangaDex',
  comick: 'Comick',
  asurascans: 'Asura Scans',
  weebcentral: 'Weeb Central',
}

const meta = {
  component: PriorityList,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: {
    items: ['mangadex', 'comick', 'asurascans', 'weebcentral'],
    disabled: ['weebcentral'],
    onChange: () => {},
    renderLabel: (id) => names[id] ?? id,
    toggleLabel: (id) => `Use ${names[id] ?? id}`,
  },
  render: function Render(args) {
    const [state, setState] = useState({ items: args.items, disabled: args.disabled })
    return (
      <div style={{ maxWidth: 420 }}>
        <PriorityList
          {...args}
          items={state.items}
          disabled={state.disabled}
          onChange={(items, disabled) => setState({ items, disabled })}
        />
      </div>
    )
  },
} satisfies Meta<typeof PriorityList>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const ToggleARowOff: Story = {
  play: async ({ canvas, userEvent }) => {
    const toggle = await canvas.findByRole('switch', { name: 'Use Comick' })
    await expect(toggle).toBeChecked()
    await userEvent.click(toggle)
    await expect(toggle).not.toBeChecked()
    await expect(canvas.getByRole('switch', { name: 'Use Weeb Central' })).not.toBeChecked()
  },
}

export const WithExtras: Story = {
  args: {
    renderExtra: (id) => (id === 'asurascans' ? <Badge size="xs" variant="light">Needs FlareSolverr</Badge> : null),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Needs FlareSolverr')).toBeVisible()
  },
}

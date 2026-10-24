import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { IconDownload, IconSearch } from '@tabler/icons-react'
import { LightActionIcon, LightBadge, LightGroup, LightStack, LightSwitch, LightText } from './lightMantine'

const meta = {
  component: LightBadge,
  tags: ['ai-generated'],
  args: { color: 'teal', children: 'Downloaded' },
} satisfies Meta<typeof LightBadge>

export default meta
type Story = StoryObj<typeof meta>

export const Badges: Story = {
  render: () => (
    <LightGroup gap={8} wrap="wrap">
      <LightBadge color="teal">Downloaded</LightBadge>
      <LightBadge color="orange" variant="outline">Missing</LightBadge>
      <LightBadge color="blue" variant="filled">Queued</LightBadge>
      <LightBadge color="grape" leftSection={<IconDownload size={12} />}>MangaDex</LightBadge>
    </LightGroup>
  ),
}

export const TextAndStacks: Story = {
  render: () => (
    <LightStack gap={4}>
      <LightText fw={600}>Chapter 12</LightText>
      <LightText c="var(--ink-3)">The long way round</LightText>
      <LightText lineClamp={1}>
        A deliberately long line that has to be cut off at the end because the cell it sits in is only so wide for the title column.
      </LightText>
    </LightStack>
  ),
}

export const Actions: Story = {
  render: () => (
    <LightGroup gap={6} wrap="nowrap">
      <LightActionIcon color="blue" variant="light" onClick={() => {}} aria-label="Search">
        <IconSearch size={16} />
      </LightActionIcon>
      <LightActionIcon color="gray" variant="subtle" onClick={() => {}} loading aria-label="Working">
        <IconDownload size={16} />
      </LightActionIcon>
      <LightActionIcon color="teal" variant="light" to="/series/1" aria-label="Open series">
        <IconSearch size={16} />
      </LightActionIcon>
    </LightGroup>
  ),
}

export const Switch: Story = {
  render: function Render() {
    const [on, setOn] = useState(true)
    return <LightSwitch checked={on} disabled={false} onChange={(e) => setOn(e.currentTarget.checked)} aria-label="Wanted" />
  },
  play: async ({ canvas, userEvent }) => {
    const toggle = await canvas.findByRole('switch', { name: 'Wanted' })
    await waitFor(() => expect(toggle).toBeChecked())
    await userEvent.click(toggle)
    await waitFor(() => expect(toggle).not.toBeChecked())
  },
}

export const DisabledSwitch: Story = {
  render: () => <LightSwitch checked disabled onChange={() => {}} aria-label="Locked" />,
}

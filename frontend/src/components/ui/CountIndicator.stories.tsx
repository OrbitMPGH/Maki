import type { Meta, StoryObj } from '@storybook/react-vite'
import { ActionIcon } from '@mantine/core'
import { IconBell } from '@tabler/icons-react'
import { expect } from 'storybook/test'
import { CountIndicator } from './CountIndicator'

const meta = {
  component: CountIndicator,
  tags: ['ai-generated'],
  args: {
    count: 3,
    children: (
      <ActionIcon variant="subtle" color="var(--neutral)" aria-label="Notifications, 3 unread">
        <IconBell size={19} />
      </ActionIcon>
    ),
  },
} satisfies Meta<typeof CountIndicator>

export default meta
type Story = StoryObj<typeof meta>

export const Brand: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('3')).toHaveAttribute('aria-hidden', 'true')
  },
}

export const Warn: Story = { args: { tone: 'warn' } }

export const Danger: Story = { args: { tone: 'danger' } }

export const Capped: Story = {
  args: { count: 240 },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('99+')).toBeInTheDocument()
  },
}

export const Zero: Story = {
  args: { count: 0 },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText('0')).toBeNull()
  },
}

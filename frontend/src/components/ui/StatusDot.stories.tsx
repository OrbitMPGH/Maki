import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { StatusDot } from './StatusDot'

const meta = {
  component: StatusDot,
  tags: ['ai-generated'],
} satisfies Meta<typeof StatusDot>

export default meta
type Story = StoryObj<typeof meta>

export const Ok: Story = {
  args: { tone: 'ok', children: 'Completed' },
  play: async ({ canvas }) => {
    await expect((await canvas.findByText('Completed')).style.getPropertyValue('--dot')).toBe('var(--ok)')
  },
}

export const Warn: Story = {
  args: { tone: 'warn', children: 'Waiting' },
}

export const Danger: Story = {
  args: { tone: 'danger', children: 'Failed' },
}

export const Live: Story = {
  args: { tone: 'info', live: true, children: 'Downloading' },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByText('Downloading')).toHaveAttribute('data-live', 'true'))
  },
}

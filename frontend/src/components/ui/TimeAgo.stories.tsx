import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { TimeAgo } from './TimeAgo'

const meta = {
  component: TimeAgo,
  tags: ['ai-generated'],
} satisfies Meta<typeof TimeAgo>

export default meta
type Story = StoryObj<typeof meta>

const ago = (ms: number) => new Date(Date.now() - ms).toISOString()

export const Minutes: Story = {
  args: { value: ago(3 * 60 * 1000) },
  play: async ({ canvas }) => {
    const el = await canvas.findByText(/3.*m.*ago|3 min/i)
    await expect(el.tagName).toBe('TIME')
  },
}

export const Yesterday: Story = {
  args: { value: ago(30 * 60 * 60 * 1000) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/yesterday/i)).toBeVisible()
  },
}

export const OlderThanAWeek: Story = {
  args: { value: ago(20 * 24 * 60 * 60 * 1000) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('time')).toBeVisible()
  },
}

export const Missing: Story = {
  args: { value: null },
}

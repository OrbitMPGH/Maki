import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Button } from '@mantine/core'
import { IconPlus } from '@tabler/icons-react'
import { PageHeader } from './PageHeader'

const meta = {
  component: PageHeader,
  tags: ['ai-generated'],
  args: { title: 'Library' },
} satisfies Meta<typeof PageHeader>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  args: {
    description: 'Every series Maki tracks, with what is new since you last read.',
    actions: <Button leftSection={<IconPlus size={16} />}>Add series</Button>,
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 1, name: 'Library' })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: /add series/i })).toBeVisible()
  },
}

export const Compact: Story = {
  args: { title: 'Activity', compact: true },
}

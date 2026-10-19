import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Anchor } from '@mantine/core'
import { IconBook, IconSparkles } from '@tabler/icons-react'
import { Link } from 'react-router-dom'
import { SectionHeader } from './SectionHeader'

const meta = {
  component: SectionHeader,
  tags: ['ai-generated'],
  args: { icon: IconBook, title: 'Continue reading' },
} satisfies Meta<typeof SectionHeader>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const WithCount: Story = {
  args: { icon: IconSparkles, title: 'Recommended', count: 24 },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('heading', { level: 2, name: 'Recommended' })).toBeVisible()
    await expect(canvas.getByText('24')).toBeVisible()
  },
}

export const WithAction: Story = {
  args: {
    icon: IconSparkles,
    title: 'Recommended',
    chevron: true,
    action: (
      <Anchor component={Link} to="/discover/recommended" size="sm">
        Find more
      </Anchor>
    ),
  },
}

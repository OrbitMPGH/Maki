import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Stack, Text } from '@mantine/core'
import { Panel } from './Panel'

const meta = {
  component: Panel,
  tags: ['ai-generated'],
  args: {
    children: (
      <Stack gap={4}>
        <Text fw={600}>Disk usage</Text>
        <Text size="sm" c="var(--ink-3)">
          842 GB free across 2 root folders.
        </Text>
      </Stack>
    ),
  },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof Panel>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('Disk usage').closest('.panel')).not.toBeNull()
  },
}

export const NoPadding: Story = {
  args: { p: 0 },
}

export const Sunken: Story = {
  args: { className: 'layer-sunken' },
}

export const SmallPadding: Story = {
  args: { p: 'xs' },
}

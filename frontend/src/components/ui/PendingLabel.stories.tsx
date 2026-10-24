import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { Button, Group } from '@mantine/core'
import { PendingLabel } from './PendingLabel'

const meta = {
  component: PendingLabel,
  tags: ['ai-generated'],
  args: { pending: 'Saving', children: 'Save changes' },
  render: (args) => (
    <Group>
      <Button>
        <PendingLabel {...args} />
      </Button>
      <Button loading>
        <PendingLabel {...args} />
      </Button>
    </Group>
  ),
} satisfies Meta<typeof PendingLabel>

export default meta
type Story = StoryObj<typeof meta>

export const IdleAndLoading: Story = {
  play: async ({ canvas }) => {
    const buttons = await canvas.findAllByRole('button')
    await waitFor(() => expect(buttons).toHaveLength(2))
    await waitFor(() => expect(buttons[1]).toHaveAttribute('data-loading', 'true'))
    await waitFor(() => expect(buttons[0]).not.toHaveAttribute('data-loading'))
  },
}

export const LongPendingLabel: Story = {
  args: { pending: 'Applying to 124 series', children: 'Apply' },
}

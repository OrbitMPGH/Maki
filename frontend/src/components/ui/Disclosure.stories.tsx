import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Checkbox, Stack } from '@mantine/core'
import { Disclosure } from './Disclosure'

const meta = {
  component: Disclosure,
  tags: ['ai-generated'],
  args: {
    title: 'Status',
    opened: false,
    onToggle: () => {},
    children: (
      <Stack gap="xs">
        <Checkbox label="Ongoing" defaultChecked />
        <Checkbox label="Completed" />
        <Checkbox label="Hiatus" />
      </Stack>
    ),
  },
  // Controlled component: hold `opened` here so the story toggles like the filter panel does.
  render: function Render(args) {
    const [opened, setOpened] = useState(args.opened)
    return <Disclosure {...args} opened={opened} onToggle={() => setOpened((o) => !o)} />
  },
} satisfies Meta<typeof Disclosure>

export default meta
type Story = StoryObj<typeof meta>

export const Closed: Story = {
  args: { count: 1, countLabel: '1 filter set', summary: 'Ongoing' },
  play: async ({ canvas, userEvent }) => {
    const head = canvas.getByRole('button', { name: /status/i })
    await expect(head).toHaveAttribute('aria-expanded', 'false')
    await expect(canvas.getByText('Ongoing', { selector: '.disclosure-summary' })).toBeVisible()
    await userEvent.click(head)
    await expect(head).toHaveAttribute('aria-expanded', 'true')
    // Collapse keeps the body aria-hidden until its open transition ends.
    await expect(await canvas.findByRole('checkbox', { name: 'Completed' })).toBeVisible()
  },
}

export const Open: Story = {
  args: { opened: true },
}

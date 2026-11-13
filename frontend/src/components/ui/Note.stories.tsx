import type { Meta, StoryObj } from '@storybook/react-vite'
import { Button } from '@mantine/core'
import { expect, fn } from 'storybook/test'
import { Note } from './Note'

const meta = {
  component: Note,
  tags: ['ai-generated'],
  args: { children: 'Two chapters want one name. Add the chapter language to the format.' },
} satisfies Meta<typeof Note>

export default meta
type Story = StoryObj<typeof meta>

export const Warn: Story = {
  args: { tone: 'warn' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('status')).toHaveAttribute('data-tone', 'warn')
  },
}

export const Danger: Story = {
  args: { tone: 'danger' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('alert')).toBeVisible()
  },
}

export const Info: Story = { args: { tone: 'info' } }

export const Ok: Story = { args: { tone: 'ok' } }

export const Neutral: Story = { args: { tone: 'neutral' } }

export const WithAction: Story = {
  args: {
    tone: 'info',
    action: (
      <Button size="compact-xs" variant="subtle">
        Restart now
      </Button>
    ),
  },
}

export const Dismissable: Story = {
  args: { tone: 'danger', closeLabel: 'Dismiss', onClose: fn() },
  play: async ({ canvas, args }) => {
    const close = await canvas.findByRole('button', { name: 'Dismiss' })
    close.click()
    await expect(args.onClose).toHaveBeenCalled()
  },
}

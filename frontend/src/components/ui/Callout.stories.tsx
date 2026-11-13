import type { Meta, StoryObj } from '@storybook/react-vite'
import { Button } from '@mantine/core'
import { expect } from 'storybook/test'
import { IconLink } from '../../lib/icons'
import { Callout } from './Callout'

const meta = {
  component: Callout,
  tags: ['ai-generated'],
  args: { children: "2 files in the series folder aren't linked to any chapter." },
} satisfies Meta<typeof Callout>

export default meta
type Story = StoryObj<typeof meta>

export const Warn: Story = {
  args: { tone: 'warn', icon: IconLink, role: 'status' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('status')).toHaveAttribute('data-tone', 'warn')
  },
}

export const WithActions: Story = {
  args: {
    tone: 'warn',
    icon: IconLink,
    actions: (
      <>
        <Button size="xs" variant="default">
          Relink files
        </Button>
        <Button size="xs" variant="subtle">
          Link by hand
        </Button>
      </>
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Relink files' })).toBeVisible()
  },
}

export const WithTitle: Story = {
  args: { tone: 'info', title: 'Sources disagree on chapter numbering' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Sources disagree on chapter numbering')).toBeVisible()
  },
}

export const Danger: Story = { args: { tone: 'danger' } }

export const Ok: Story = { args: { tone: 'ok' } }

export const Brand: Story = { args: { tone: 'brand' } }

export const Neutral: Story = { args: { tone: 'neutral' } }

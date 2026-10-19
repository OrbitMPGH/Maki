import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { TagChip } from './TagChip'

const meta = {
  component: TagChip,
  tags: ['ai-generated'],
} satisfies Meta<typeof TagChip>

export default meta
type Story = StoryObj<typeof meta>

export const Plain: Story = {
  args: { children: 'Romance' },
}

export const WithDot: Story = {
  args: { children: 'Action', dot: 'var(--danger)' },
}

export const Small: Story = {
  args: { children: 'Isekai', size: 'sm' },
}

export const Toggle: Story = {
  args: { children: 'Comedy', active: false, onClick: fn() },
  play: async ({ args, canvas, userEvent }) => {
    const chip = canvas.getByRole('button', { name: /comedy/i })
    await expect(chip).toHaveAttribute('aria-pressed', 'false')
    await userEvent.click(chip)
    await expect(args.onClick).toHaveBeenCalledOnce()
  },
}

export const AsLink: Story = {
  args: { children: 'Drama', href: '/library?tag=drama' },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('link', { name: /drama/i })).toHaveAttribute('href', '/library?tag=drama')
  },
}

export const CssCheck: Story = {
  args: { children: 'Fantasy' },
  play: async ({ canvas }) => {
    const chip = canvas.getByText('Fantasy').closest('.tag-chip')!
    // theme.css gives .tag-chip `padding: 3px 10px` and a 1px border; unset if theme.css did not load.
    await expect(getComputedStyle(chip).paddingLeft).toBe('10px')
    await expect(getComputedStyle(chip).borderTopWidth).toBe('1px')
  },
}

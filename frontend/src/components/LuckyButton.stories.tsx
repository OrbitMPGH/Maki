import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { cover } from '../../.storybook/mocks/api'
import { seedTitles } from '../../.storybook/mocks/discover'
import { LuckyButton } from './LuckyButton'

const candidates = seedTitles.slice(0, 8).map((s, i) => ({
  key: String(30000 + i),
  title: s.title,
  coverUrl: cover(s.title),
}))

const meta = {
  component: LuckyButton,
  tags: ['ai-generated'],
  args: { candidates, onPick: fn() },
  parameters: { layout: 'centered' },
} satisfies Meta<typeof LuckyButton>

export default meta
type Story = StoryObj<typeof meta>

export const Icon: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Feeling lucky' })).toBeVisible()
  },
}

export const RollsAndPicks: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Feeling lucky' }))
    await waitFor(() => expect(args.onPick).toHaveBeenCalledOnce(), { timeout: 6000 })
    await expect(args.onPick).toHaveBeenCalledWith(expect.stringMatching(/^3000[0-7]$/))
  },
}

export const NothingToRoll: Story = {
  args: { candidates: [] },
  play: async ({ args, canvas, userEvent }) => {
    const button = await canvas.findByRole('button', { name: 'Nothing to roll' })
    await expect(button).toHaveAttribute('aria-disabled', 'true')
    await userEvent.click(button)
    await expect(args.onPick).not.toHaveBeenCalled()
  },
}

export const Pill: Story = {
  args: { variant: 'pill', onDismiss: fn() },
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByRole('button', { name: 'Roll again' })).toBeVisible()
    await userEvent.click(canvas.getByRole('button', { name: 'Close' }))
    await expect(args.onDismiss).toHaveBeenCalled()
  },
}

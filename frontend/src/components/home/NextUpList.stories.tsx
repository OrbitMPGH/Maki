import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, within } from 'storybook/test'
import { continueReading, jumpBackIn } from '../../../.storybook/mocks/home'
import { NextUpList } from './NextUpList'

const meta = {
  component: NextUpList,
  tags: ['ai-generated'],
  args: { items: continueReading.slice(1), rail: 'continue' },
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 340, height: 420 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof NextUpList>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    const list = await canvas.findByRole('region', { name: 'Next up' })
    await expect(within(list).getByRole('heading', { level: 2, name: 'Next up' })).toBeVisible()
    await expect(within(list).getByRole('link', { name: /Ashen Crown Regression/ })).toHaveAttribute('href', expect.stringContaining('/read/'))
  },
}

export const JumpBackIn: Story = {
  args: { items: jumpBackIn, rail: 'jumpback' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('region', { name: 'Next to jump back in' })).toBeVisible()
  },
}

export const ScrollsInsideTheCard: Story = {
  args: { items: [...continueReading, ...jumpBackIn] },
  play: async ({ canvas }) => {
    const list = await canvas.findByRole('region', { name: 'Next up' })
    await expect(list).toBeVisible()
  },
}

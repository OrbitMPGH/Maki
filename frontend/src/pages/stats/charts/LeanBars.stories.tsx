import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { insightsFor } from '../../../../.storybook/mocks/insights'
import { Panel } from '../../../components/ui/Panel'
import { LeanBars } from './LeanBars'

const lean = insightsFor('2026-09-20', '2026-10-19', 'rich').taste.lean

const meta = {
  component: LeanBars,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md" style={{ maxWidth: 560 }}>
        <Story />
      </Panel>
    ),
  ],
  args: { items: lean },
} satisfies Meta<typeof LeanBars>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('+13 pt')).toBeVisible()
    await expect(canvas.getByText('−8 pt')).toBeVisible()
  },
}

export const TopThree: Story = { args: { max: 3 } }

export const NoLeanAtAll: Story = {
  args: { items: [{ name: 'Fantasy', readShare: 0.2, libraryShare: 0.2 }] },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('0 pt')).toBeVisible()
  },
}

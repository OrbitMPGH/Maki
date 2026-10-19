import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { standingFor } from '../../../../.storybook/mocks/insights'
import { Panel } from '../../../components/ui/Panel'
import { RatingDumbbell } from './RatingDumbbell'

const series = standingFor('rich').ratings!.series

const meta = {
  component: RatingDumbbell,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md" style={{ maxWidth: 560 }}>
        <Story />
      </Panel>
    ),
  ],
  args: { series },
} satisfies Meta<typeof RatingDumbbell>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('Snowfield Letters')).toBeVisible()
  },
}

export const AgreeingWithTheCrowd: Story = {
  args: { series: series.filter((s) => s.yours === s.community) },
}

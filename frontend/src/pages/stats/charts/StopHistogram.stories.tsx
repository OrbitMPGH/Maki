import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { Panel } from '../../../components/ui/Panel'
import { StopHistogram } from './StopHistogram'

const meta = {
  component: StopHistogram,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md" style={{ maxWidth: 520 }}>
        <Story />
      </Panel>
    ),
  ],
  args: { buckets: [3, 4, 3, 2, 1, 1, 0, 1, 0, 0], medianFraction: 0.34 },
} satisfies Meta<typeof StopHistogram>

export default meta
type Story = StoryObj<typeof meta>

/** The bucket holding the median is flagged and tinted. */
export const Default: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByText('Median')).toHaveLength(1))
  },
}

export const LateStoppers: Story = {
  args: { buckets: [0, 0, 1, 0, 1, 2, 3, 5, 6, 2], medianFraction: 0.82 },
}

export const NoMedian: Story = {
  args: { medianFraction: null },
  play: async ({ canvas }) => {
    await expect(canvas.queryByText('Median')).toBeNull()
  },
}

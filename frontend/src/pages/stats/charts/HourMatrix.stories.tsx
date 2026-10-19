import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { richRhythm } from '../../../../.storybook/mocks/insights'
import { Panel } from '../../../components/ui/Panel'
import { HourMatrix } from './HourMatrix'

const meta = {
  component: HourMatrix,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md" style={{ maxWidth: 720 }}>
        <Story />
      </Panel>
    ),
  ],
  args: { secondsByWeekdayHour: richRhythm },
} satisfies Meta<typeof HourMatrix>

export default meta
type Story = StoryObj<typeof meta>

export const Evenings: Story = {
  play: async ({ canvasElement, canvas }) => {
    await expect(canvasElement.querySelectorAll('.stats-matrix-cell')).toHaveLength(168)
    await expect(canvas.getByText(/Less/)).toBeVisible()
  },
}

/** One late-night Saturday sitting and nothing else. */
export const SingleSitting: Story = {
  args: { secondsByWeekdayHour: Array.from({ length: 168 }, (_, i) => (i === 5 * 24 + 23 ? 5400 : 0)) },
}

export const Empty: Story = {
  args: { secondsByWeekdayHour: Array.from({ length: 168 }, () => 0) },
}

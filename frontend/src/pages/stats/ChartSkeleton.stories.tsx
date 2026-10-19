import type { Meta, StoryObj } from '@storybook/react-vite'
import { Panel } from '../../components/ui/Panel'
import { ChartSkeleton } from './ChartSkeleton'

const meta = {
  component: ChartSkeleton,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md" style={{ maxWidth: 640 }}>
        <Story />
      </Panel>
    ),
  ],
  args: { h: 240 },
} satisfies Meta<typeof ChartSkeleton>

export default meta
type Story = StoryObj<typeof meta>

export const Tall: Story = {}

export const Short: Story = { args: { h: 80 } }

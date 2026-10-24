import type { Meta, StoryObj } from '@storybook/react-vite'
import { TableSkeleton } from './TableSkeleton'

const meta = {
  component: TableSkeleton,
  tags: ['ai-generated'],
  args: { columns: 5 },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof TableSkeleton>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const FewRows: Story = {
  args: { columns: 3, rows: 3 },
}

export const WideTable: Story = {
  args: { columns: 8, rows: 10 },
}

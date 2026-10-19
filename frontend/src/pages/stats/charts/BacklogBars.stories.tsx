import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { standingFor } from '../../../../.storybook/mocks/insights'
import { Panel } from '../../../components/ui/Panel'
import { BacklogBars } from './BacklogBars'

const top = standingFor('rich').backlog.top

const meta = {
  component: BacklogBars,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md" style={{ maxWidth: 640 }}>
        <Story />
      </Panel>
    ),
  ],
  args: { items: top },
} satisfies Meta<typeof BacklogBars>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('210 left')).toBeVisible()
    await expect(canvas.getByText('11 left')).toBeVisible()
  },
}

export const SingleSeries: Story = {
  args: { items: top.slice(0, 1) },
}

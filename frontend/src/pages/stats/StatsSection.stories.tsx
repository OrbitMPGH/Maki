import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { IconBook2, IconBooks } from '@tabler/icons-react'
import { Panel } from '../../components/ui/Panel'
import { StatsInsight, StatsSection } from './StatsSection'

const meta = {
  component: StatsSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: {
    sectionKey: 'reading',
    icon: IconBook2,
    title: 'Reading',
    children: <Panel p="md">Panels sit here.</Panel>,
  },
  decorators: [
    (Story) => (
      <div className="stats-layout">
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof StatsSection>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('region', { name: 'Reading' })).toBeVisible()
  },
}

export const WithInsight: Story = {
  args: {
    insight: (
      <StatsInsight>
        <em>128 chapters</em> across <em>21 reading days</em>
        <span className="stats-insight-dim">, most of it after 9 PM</span>.
      </StatsInsight>
    ),
  },
}

/** Sections that ignore the reader or the window say so beside the title. */
export const WithScope: Story = {
  args: { sectionKey: 'library', icon: IconBooks, title: 'Library', scope: 'Whole library' },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('Whole library')).toBeVisible()
  },
}

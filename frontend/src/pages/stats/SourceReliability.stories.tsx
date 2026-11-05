import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { sourceReliability, sourceUsage } from '../../../.storybook/mocks/insights'
import { Panel } from '../../components/ui/Panel'
import { SourceReliability } from './SourceReliability'

const meta = {
  component: SourceReliability,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md">
        <Story />
      </Panel>
    ),
  ],
  args: { sources: sourceUsage, reliability: sourceReliability },
} satisfies Meta<typeof SourceReliability>

export default meta
type Story = StoryObj<typeof meta>

/** Asura Scans sits under 80% success, so its rate is drawn in the warning colour. */
export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('columnheader', { name: 'Median wait' })).toBeVisible()
    await expect(await canvas.findByText('MangaDex')).toBeVisible()
    await expect(await canvas.findByText('Torrent')).toBeVisible()
  },
}

/** Sources on disk with no download in the last 30 days have nothing to rate, so those columns go. */
export const NoRecentDownloads: Story = {
  args: { reliability: [] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('MangaDex')).toBeVisible()
    await expect(canvas.queryByRole('columnheader', { name: 'Success' })).toBeNull()
    await expect(canvas.queryByRole('columnheader', { name: 'Median wait' })).toBeNull()
  },
}

export const NothingDownloaded: Story = {
  args: { sources: [], reliability: [] },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Nothing downloaded yet.')).toBeVisible()
  },
}

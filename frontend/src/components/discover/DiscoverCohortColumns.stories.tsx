import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import type { RecommendationItem } from '../../api/hooks'
import { cohortRail, discoverPage } from '../../../.storybook/mocks/discover'
import { DiscoverCohortColumns } from './DiscoverCohortColumns'

const meta = {
  component: DiscoverCohortColumns,
  tags: ['ai-generated'],
  args: {
    rail: cohortRail,
    onOpen: fn(),
    onSeeAll: fn(),
    seriesIdFor: (_item: RecommendationItem): number | null => null,
  },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof DiscoverCohortColumns>

export default meta
type Story = StoryObj<typeof meta>

export const Groups: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('42%')).toBeVisible()
    const seeAll = await canvas.findAllByRole('button', { name: /See all/ })
    await userEvent.click(seeAll[1])
    await waitFor(() => expect(args.onSeeAll).toHaveBeenCalledWith(7))
  },
}

/** A reader placed with one group gets the picks sorted three ways. */
export const OneGroup: Story = {
  args: { rail: { ...cohortRail, groups: cohortRail.groups?.slice(0, 1) } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Most of them finished')).toBeVisible()
  },
}

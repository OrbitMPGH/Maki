import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, userEvent, within } from 'storybook/test'
import { Panel } from '../ui/Panel'
import {
  announcedOutlook,
  expectedOutlook,
  finishedOutlook,
  irregularOutlook,
  lateOutlook,
  mediumOutlook,
  unknownOutlook,
} from '../../../.storybook/mocks/operations/upcoming'
import { ReleaseOutlookSummary, ReleaseOutlookView } from './ReleaseOutlookSummary'

const meta = {
  component: ReleaseOutlookView,
  tags: ['ai-generated'],
  decorators: [
    (Story) => (
      <Panel maw={420}>
        <Story />
      </Panel>
    ),
  ],
  args: { outlook: expectedOutlook },
} satisfies Meta<typeof ReleaseOutlookView>

export default meta
type Story = StoryObj<typeof meta>

export const Expected: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/^Likely /)).toBeVisible()
    await expect(await canvas.findByText('High confidence')).toBeVisible()
    await expect(await canvas.findByText(/Usually every 7 days/)).toBeVisible()
    await expect(await canvas.findByText(/Checked every 2 hours/)).toBeVisible()
  },
}

export const ConfidenceExplained: Story = {
  play: async ({ canvas }) => {
    await userEvent.hover(await canvas.findByText('High confidence'))
    await expect(await within(document.body).findByText('9 of the last 10 chapters came out inside the window drawn for them.')).toBeVisible()
  },
}

export const AnnouncedByTheSource: Story = {
  args: { outlook: announcedOutlook },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/MANGA Plus has it scheduled for/)).toBeVisible()
    await expect(await canvas.findByText(/^Likely /)).toBeVisible()
  },
}

export const MediumConfidence: Story = {
  args: { outlook: mediumOutlook },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Medium confidence')).toBeVisible()
    await expect(await canvas.findByText(/Checked every 6 hours/)).toBeVisible()
  },
}

export const RunningLate: Story = {
  args: { outlook: lateOutlook },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/^Later than expected\. We guessed /)).toBeVisible()
  },
}

export const Irregular: Story = {
  args: { outlook: irregularOutlook },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Releases too irregularly to estimate.')).toBeVisible()
    await expect(canvas.queryByText(/confidence/)).toBeNull()
  },
}

export const NoHistoryYet: Story = {
  args: { outlook: unknownOutlook },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Not enough release history for an estimate yet.')).toBeVisible()
  },
}

export const NotChecked: Story = {
  args: { outlook: finishedOutlook },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Not checked for new chapters/)).toBeVisible()
  },
}

/** The series page's wrapper, fetching from `/series/:id/outlook`. */
export const Fetched: StoryObj<typeof ReleaseOutlookSummary> = {
  render: () => <ReleaseOutlookSummary seriesId={1} />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/MANGA Plus has it scheduled for/)).toBeVisible()
  },
}

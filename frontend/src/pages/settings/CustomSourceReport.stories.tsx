import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { CustomSourceReport } from './CustomSourceReport'

const meta = {
  component: CustomSourceReport,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 560 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof CustomSourceReport>

export default meta
type Story = StoryObj<typeof meta>

export const LockedAndGroups: Story = {
  args: {
    report: {
      passed: true,
      steps: [
        { name: 'series', status: 'ok', error: null, detail: { title: 'Lanterns Over Kessel Bay' } },
        {
          name: 'chapters',
          status: 'ok',
          error: null,
          detail: { count: 84, unnumbered: 0, locked: 3, groups: ['Tidewater Scans', 'Night Owl TL'] },
        },
      ],
    },
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('84 chapters')).toBeInTheDocument()
    await expect(await canvas.findByText('3 chapters listed as locked')).toBeInTheDocument()
    await expect(await canvas.findByText('Groups credited: Tidewater Scans, Night Owl TL')).toBeInTheDocument()
  },
}

export const SampleChapterLocked: Story = {
  args: {
    report: {
      passed: false,
      steps: [
        { name: 'chapters', status: 'ok', error: null, detail: { count: 12, locked: 1, groups: [] } },
        {
          name: 'pages',
          status: 'failed',
          error: 'The sample chapter is listed as locked. Pick a free chapter as the sample.',
          detail: null,
        },
        { name: 'image', status: 'skipped', error: null, detail: null },
      ],
    },
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Some checks failed')).toBeInTheDocument()
    await expect(await canvas.findByText(/listed as locked\. Pick a free chapter/)).toBeInTheDocument()
  },
}

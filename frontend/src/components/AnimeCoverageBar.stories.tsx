import type { Meta, StoryObj } from '@storybook/react-vite'
import { AnimeCoverageBar } from './AnimeCoverageBar'

const meta = {
  component: AnimeCoverageBar,
  tags: ['ai-generated'],
  args: {
    start: 'Vol 1, Chap 1 (S1) / Vol 5, Chap 39 (S2) / Vol 9, Chap 80 (Film + OVA)',
    end: 'Vol 5, Chap 38 (S1) / Vol 9, Chap 79 (S2) / Vol 10, Chap 90 (Film + OVA)',
    totalChapters: 142,
    readChapter: 40,
  },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 720 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof AnimeCoverageBar>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {}

export const NoProgress: Story = {
  args: { readChapter: undefined },
}

export const ResumeHintHidden: Story = {
  args: { hideResumeHint: true },
}

export const UnknownTotal: Story = {
  args: { totalChapters: null },
}

export const Unparseable: Story = {
  args: { start: 'Alternate setting with an original ending', end: null },
}

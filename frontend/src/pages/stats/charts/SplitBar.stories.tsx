import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { Panel } from '../../../components/ui/Panel'
import { SplitBar } from './SplitBar'

const meta = {
  component: SplitBar,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  decorators: [
    (Story) => (
      <Panel p="md" style={{ maxWidth: 420 }}>
        <Story />
      </Panel>
    ),
  ],
  args: {
    items: [
      { key: 'manga', label: 'Manga', value: 312 },
      { key: 'manhwa', label: 'Manhwa', value: 148 },
      { key: 'manhua', label: 'Manhua', value: 37 },
    ],
  },
} satisfies Meta<typeof SplitBar>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('63%')).toBeVisible()
  },
}

/** More categories than colours: the tail folds into Other. */
export const FoldsIntoOther: Story = {
  args: {
    items: [
      { key: 'a', label: 'Fantasy', value: 41 },
      { key: 'b', label: 'Action', value: 33 },
      { key: 'c', label: 'Romance', value: 27 },
      { key: 'd', label: 'Slice of Life', value: 22 },
      { key: 'e', label: 'Drama', value: 18 },
      { key: 'f', label: 'Comedy', value: 15 },
      { key: 'g', label: 'Horror', value: 4 },
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Other')).toBeVisible()
  },
}

export const SingleCategory: Story = {
  args: { items: [{ key: 'manga', label: 'Manga', value: 98 }] },
}

/** Nothing above zero draws nothing, not an empty bar. */
export const AllZero: Story = {
  args: { items: [{ key: 'manga', label: 'Manga', value: 0 }] },
  play: async ({ canvasElement }) => {
    await expect(canvasElement.querySelector('.stats-split')).toBeNull()
  },
}

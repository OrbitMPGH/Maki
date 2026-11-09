import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { cover } from '../../../.storybook/mocks/api'
import ChapterBanner from './ChapterBanner'

const meta = {
  component: ChapterBanner,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    seriesTitle: 'Lanterns Over Kasumi',
    label: 'Chapter 133',
    pageCount: 18,
    coverUrl: cover(3040),
  },
  decorators: [
    (Story) => (
      <div className="reader-root">
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof ChapterBanner>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    // It removes itself after a couple of seconds, so this has to look straight away.
    await waitFor(() => expect(canvas.getByRole('status')).toHaveTextContent('Chapter 133'))
    await expect(await canvas.findByText('18 pages')).toBeInTheDocument()
  },
}

export const SinglePage: Story = {
  args: { label: 'Special: Midnight edition', pageCount: 1 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('1 page')).toBeInTheDocument()
  },
}

export const LongTitles: Story = {
  args: {
    seriesTitle: 'Grave Robber of the Seventh Hall: The Complete Expedition Record',
    label: 'Chapter 22: What the lanterns remember when nobody is left to carry them',
    pageCount: 42,
  },
}

export const NoCover: Story = {
  args: { coverUrl: null },
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByText('Chapter 133')).toBeInTheDocument()
    await expect(canvasElement.querySelector('img')).toBeNull()
  },
}

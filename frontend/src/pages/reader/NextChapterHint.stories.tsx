import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import NextChapterHint from './NextChapterHint'

const meta = {
  component: NextChapterHint,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    progress: 0.55,
    pulling: false,
    atEnd: false,
    nextLabel: 'Chapter 135',
  },
  decorators: [
    (Story) => (
      <div className="reader-root">
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof NextChapterHint>

export default meta
type Story = StoryObj<typeof meta>

export const Scrolling: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByText('Scroll')).toBeVisible()
    await expect(canvas.getByText('Chapter 135')).toBeVisible()
  },
}

export const Pulling: Story = {
  args: { pulling: true },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('Pull up')).toBeVisible()
  },
}

export const ReadyToRelease: Story = {
  args: { pulling: true, progress: 1 },
  play: async ({ canvas, canvasElement }) => {
    await expect(canvas.getByText('Release')).toBeVisible()
    await expect(canvasElement.querySelector('[data-state="ready"]')).not.toBeNull()
  },
}

/** The meter filled and the next chapter is loading: full, solid, whatever the input does now. */
export const Opening: Story = {
  args: { opening: true, progress: 0.2 },
  play: async ({ canvas, canvasElement }) => {
    await expect(canvas.getByText('Opening')).toBeVisible()
    await expect(canvasElement.querySelector('[data-state="ready"]')).not.toBeNull()
  },
}

export const LastChapter: Story = {
  args: { atEnd: true, nextLabel: null, progress: 0 },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('No more chapters')).toBeVisible()
  },
}

export const NoNextLabel: Story = {
  args: { nextLabel: null, pulling: true },
  play: async ({ canvas }) => {
    await expect(canvas.getByText('Pull up for next chapter')).toBeVisible()
  },
}

export const LongLabel: Story = {
  args: { nextLabel: 'Chapter 22: What the lanterns remember when nobody is left to carry them' },
}

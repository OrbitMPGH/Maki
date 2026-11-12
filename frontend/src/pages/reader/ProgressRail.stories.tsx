import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, userEvent, waitFor } from 'storybook/test'
import { pageUrlsFor } from '../../../.storybook/mocks/entry/fixtures'
import ContinuousView from './ContinuousView'
import ProgressRail from './ProgressRail'
import { createStripTracker } from './stripPosition'

function RailOverStrip({ urls, active, strip }: { urls: string[]; active: boolean; strip?: boolean }) {
  const [tracker] = useState(createStripTracker)
  const [page, setPage] = useState(0)
  return (
    <div className="reader-root">
      <div className="reader-surface" data-scroll="vertical">
        <ContinuousView
          urls={urls}
          page={page}
          onPageChange={setPage}
          seekVersion={0}
          onPastEnd={fn()}
          hasNext
          fit="width"
          scale={100}
          gap={0}
          label="Chapter 40"
          tracker={tracker}
        />
      </div>
      <ProgressRail tracker={tracker} count={urls.length} active={active} strip={strip} />
    </div>
  )
}

const meta = {
  component: RailOverStrip,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: { urls: pageUrlsFor(3040, 40), active: true },
} satisfies Meta<typeof RailOverStrip>

export default meta
type Story = StoryObj<typeof meta>

const scroller = (canvasElement: HTMLElement) => canvasElement.querySelector<HTMLElement>('.reader-surface')!

/** With the bars up: a track with a handle that takes the keyboard and the pointer. */
export const Active: Story = {
  play: async ({ canvas, canvasElement }) => {
    const rail = await canvas.findByRole('slider', { name: 'Chapter progress' })
    await waitFor(() => expect(rail).toHaveAttribute('aria-valuenow', '1'))
    // Until a page has loaded the rest have no shape yet, and the end is nowhere near its real place.
    await waitFor(() => expect(canvasElement.querySelector('.reader-continuous')).toHaveAttribute('data-shaped'))
    rail.focus()
    await userEvent.keyboard('{End}')
    await waitFor(() => {
      const view = scroller(canvasElement)
      expect(view.scrollHeight - view.scrollTop - view.clientHeight).toBeLessThan(2)
    })
    await waitFor(() => expect(rail.style.getPropertyValue('--rail-progress')).toBe('100%'))
    await userEvent.keyboard('{Home}')
    await waitFor(() => expect(scroller(canvasElement).scrollTop).toBe(0))
  },
}

/** While reading, a hairline that follows the strip and takes no touches. */
export const Reading: Story = {
  args: { active: false },
  play: async ({ canvas, canvasElement }) => {
    const rail = await canvas.findByRole('slider', { name: 'Chapter progress' })
    await expect(rail).not.toHaveAttribute('data-active')
    await expect(rail).toHaveAttribute('tabindex', '-1')
    const view = scroller(canvasElement)
    await waitFor(() => expect(view.scrollHeight).toBeGreaterThan(view.clientHeight * 4))
    view.scrollTop = view.scrollHeight / 2
    await waitFor(() => expect(Number(rail.getAttribute('aria-valuenow'))).toBeGreaterThan(10))
  },
}

/** Five slices three screens tall: each page is an equal share, and the rail moves within one. */
export const TallSlices: Story = {
  args: { urls: Array.from({ length: 5 }, (_, index) => `/mock/strip/tall/${index + 1}.svg`) },
  play: async ({ canvas, canvasElement }) => {
    const rail = await canvas.findByRole('slider', { name: 'Chapter progress' })
    const view = scroller(canvasElement)
    await waitFor(() => expect(view.querySelector('img[data-loaded]')).not.toBeNull())
    const first = view.querySelector<HTMLImageElement>('img.reader-page')!
    view.scrollTop = first.offsetHeight * 0.25
    await waitFor(() => {
      const share = parseFloat(rail.style.getPropertyValue('--rail-progress'))
      expect(share).toBeGreaterThan(5)
      expect(share).toBeLessThan(20)
    })
    await expect(rail).toHaveAttribute('aria-valuenow', '1')
  },
}

/** With the thumbnail strip open, the rail ends above it. */
export const AboveTheThumbnailStrip: Story = {
  args: { strip: true },
}

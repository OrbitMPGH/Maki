import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { pageUrlsFor } from '../../../.storybook/mocks/entry/fixtures'
import { toneFilter } from './prefs'
import ContinuousView from './ContinuousView'

const meta = {
  component: ContinuousView,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    urls: pageUrlsFor(3040, 12),
    page: 0,
    onPageChange: fn(),
    seekVersion: 0,
    onPastEnd: fn(),
    hasNext: true,
    fit: 'width',
    scale: 100,
    gap: 0,
    label: 'Chapter 40',
  },
  decorators: [
    (Story) => (
      <div className="reader-root">
        <div className="reader-surface" data-scroll="vertical">
          <Story />
        </div>
      </div>
    ),
  ],
} satisfies Meta<typeof ContinuousView>

export default meta
type Story = StoryObj<typeof meta>

export const Strip: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Chapter 40 - page 1' })).toBeVisible()
  },
}

export const WithGapBetweenPages: Story = {
  args: { gap: 24 },
}

export const ResumesPartWayDown: Story = {
  args: { page: 6, seekVersion: 1 },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Chapter 40 - page 7' })).toBeInTheDocument()
  },
}

/** At the bottom, the next chapter takes one pull of about a quarter of the screen, not a wheel-sized drag. */
export const TouchPullOpensNextChapter: Story = {
  play: async ({ canvasElement, args }) => {
    const scroller = canvasElement.querySelector<HTMLElement>('.reader-surface')!
    await waitFor(() => expect(scroller.scrollHeight).toBeGreaterThan(scroller.clientHeight * 2))
    scroller.scrollTop = scroller.scrollHeight
    await waitFor(() =>
      expect(scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight).toBeLessThan(2),
    )

    const touch = (y: number) => new Touch({ identifier: 1, target: scroller, clientX: 100, clientY: y })
    const pull = Math.ceil(Math.max(140, scroller.clientHeight * 0.25))
    const start = scroller.clientHeight - 20
    scroller.dispatchEvent(new TouchEvent('touchstart', { touches: [touch(start)], bubbles: true }))
    // Half a pull fills half the meter and does not navigate yet.
    scroller.dispatchEvent(new TouchEvent('touchmove', { touches: [touch(start - pull / 2)], bubbles: true }))
    await expect(args.onPastEnd).not.toHaveBeenCalled()
    scroller.dispatchEvent(new TouchEvent('touchmove', { touches: [touch(start - pull)], bubbles: true }))
    await waitFor(() => expect(args.onPastEnd).toHaveBeenCalledTimes(1))
  },
}

export const LastChapter: Story = {
  args: { hasNext: false, page: 11, seekVersion: 1 },
}

export const SepiaAndDimmed: Story = {
  args: { tone: toneFilter({ dim: 20, filter: 'sepia' }) },
}

export const OriginalSize: Story = {
  args: { fit: 'original', scale: 100 },
}

export const PageFailsToLoad: Story = {
  args: { urls: pageUrlsFor(4404, 4), label: 'Chapter 61' },
  parameters: {
    msw: [http.get(api('/reader/chapter/4404/page/:n'), () => new HttpResponse(null, { status: 500 }))],
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect((canvas.getAllByText('This page failed to load.')).length).toBeGreaterThan(0))
  },
}

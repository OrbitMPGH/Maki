import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, type Mock } from 'storybook/test'
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
    onEnhanceRange: fn(),
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

/**
 * Scrolls the strip to its end once the pages there have loaded: the meter waits for them. Lazy
 * pages the jump skipped above the viewport never load and the meter ignores them, so this does too.
 */
async function scrollToEnd(canvasElement: HTMLElement): Promise<HTMLElement> {
  const scroller = canvasElement.querySelector<HTMLElement>('.reader-surface')!
  await waitFor(() => expect(scroller.scrollHeight).toBeGreaterThan(scroller.clientHeight * 2))
  const atEnd = () => expect(scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight).toBeLessThan(2)
  scroller.scrollTop = scroller.scrollHeight
  await waitFor(atEnd)
  const pages = [...canvasElement.querySelectorAll<HTMLImageElement>('img.reader-page')]
  const pending = () => {
    const top = scroller.getBoundingClientRect().top
    return pages.filter((page) => !page.complete && page.getBoundingClientRect().bottom >= top).length
  }
  await waitFor(() => expect(pending()).toBe(0))
  scroller.scrollTop = scroller.scrollHeight
  await waitFor(atEnd)
  return scroller
}

export const Strip: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Chapter 40 - page 1' })).toBeVisible()
  },
}

/**
 * Pages that have not loaded borrow the shape of the ones that have, so the strip is about its
 * real length early and the scrollbar does not run away as images arrive.
 */
export const UnloadedPagesBorrowTheLoadedShape: Story = {
  play: async ({ canvasElement }) => {
    const pages = () => [...canvasElement.querySelectorAll<HTMLImageElement>('img.reader-page')]
    await waitFor(() => expect(pages()[0]).toHaveAttribute('data-loaded'))
    await waitFor(() => expect(canvasElement.querySelector('.reader-continuous')).toHaveAttribute('data-shaped'))
    const last = pages().at(-1)!
    await expect(last).not.toHaveAttribute('data-loaded')
    await waitFor(() =>
      expect(Math.abs(last.offsetHeight - pages()[0].offsetHeight)).toBeLessThan(pages()[0].offsetHeight * 0.02),
    )
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

/**
 * At the bottom, touch is pull and release: about a quarter of the screen fills the meter, and
 * only letting go with it full opens the next chapter. Letting go early just empties it.
 */
export const TouchPullAndRelease: Story = {
  play: async ({ canvas, canvasElement, args }) => {
    const scroller = await scrollToEnd(canvasElement)

    const touch = (y: number) => new Touch({ identifier: 1, target: scroller, clientX: 100, clientY: y })
    const send = (type: string, y?: number) =>
      scroller.dispatchEvent(
        new TouchEvent(type, { touches: y == null ? [] : [touch(y)], changedTouches: [touch(y ?? 0)], bubbles: true }),
      )
    const pull = Math.ceil(Math.max(140, scroller.clientHeight * 0.25))
    const start = scroller.clientHeight - 20

    // Half a pull, then letting go: the meter empties and nothing navigates.
    send('touchstart', start)
    send('touchmove', start - pull / 2)
    await expect(await canvas.findByText('Pull up for next chapter')).toBeVisible()
    send('touchend')
    await waitFor(() => expect(canvas.queryByText('Pull up for next chapter')).toBeNull())
    await expect(args.onPastEnd).not.toHaveBeenCalled()

    // A full pull says so, and still waits for the release.
    send('touchstart', start)
    send('touchmove', start - pull)
    await expect(await canvas.findByText('Release for next chapter')).toBeVisible()
    await expect(args.onPastEnd).not.toHaveBeenCalled()
    send('touchend')
    await waitFor(() => expect(args.onPastEnd).toHaveBeenCalledTimes(1))
  },
}

/**
 * At the bottom, a single wheel notch (the tail of a fling that lands there) shows nothing. A
 * deliberate push does, and scrolling back up fades the card out instead of dropping it, so
 * scrolling down again picks up the same card rather than flashing in a new one.
 */
export const WheelNudgeStaysQuiet: Story = {
  play: async ({ canvas, canvasElement }) => {
    const scroller = await scrollToEnd(canvasElement)
    const wheel = (deltaY: number) => scroller.dispatchEvent(new WheelEvent('wheel', { deltaY, bubbles: true }))
    const settle = () => new Promise((resolve) => setTimeout(resolve, 100))

    wheel(100)
    await settle()
    expect(canvas.queryByText('Scroll for next chapter')).toBeNull()

    wheel(100)
    const card = await canvas.findByText('Scroll for next chapter')
    wheel(-100)
    await settle()
    expect(canvasElement.querySelector('.reader-next-chapter-hint')).toHaveAttribute('data-leaving')
    wheel(200)
    await waitFor(() => expect(canvasElement.querySelector('.reader-next-chapter-hint')).not.toHaveAttribute('data-leaving'))
    expect(canvas.getByText('Scroll for next chapter')).toBe(card)

    wheel(-100)
    await waitFor(() => expect(canvas.queryByText('Scroll for next chapter')).toBeNull())
  },
}

/**
 * A fast scroll keeps sending wheel events after the meter fills. While the next chapter loads the
 * ring stays full and the extra input neither restarts it nor asks for the chapter again.
 */
export const FullMeterHoldsWhileOpening: Story = {
  args: { onPastEnd: fn(() => true), nextLabel: 'Chapter 41' },
  play: async ({ canvas, canvasElement, args }) => {
    const scroller = await scrollToEnd(canvasElement)
    const wheel = (deltaY: number) => scroller.dispatchEvent(new WheelEvent('wheel', { deltaY, bubbles: true }))

    for (let i = 0; i < 25; i++) wheel(120)
    await waitFor(() => expect(args.onPastEnd).toHaveBeenCalledTimes(1))
    await expect(await canvas.findByText('Opening')).toBeVisible()
    const hint = canvasElement.querySelector('.reader-next-chapter-hint')!
    await waitFor(() => expect(hint).toHaveAttribute('data-state', 'ready'))
    await expect(hint).not.toHaveAttribute('data-leaving')
    await expect(args.onPastEnd).toHaveBeenCalledTimes(1)
  },
}

/**
 * The last page landing taller than its placeholder moves the bottom away mid-fill. The meter holds
 * while the reader scrolls on down and carries on from there, rather than emptying and starting over.
 */
export const MeterHoldsWhenTheEndGrows: Story = {
  args: { onPastEnd: fn(() => true), nextLabel: 'Chapter 41' },
  play: async ({ canvasElement, args }) => {
    const scroller = await scrollToEnd(canvasElement)
    const pages = [...canvasElement.querySelectorAll<HTMLImageElement>('img.reader-page')]
    const wheel = (deltaY: number) => scroller.dispatchEvent(new WheelEvent('wheel', { deltaY, bubbles: true }))

    for (let i = 0; i < 5; i++) wheel(120)
    const hint = await waitFor(() => {
      const element = canvasElement.querySelector('.reader-next-chapter-hint')
      expect(element).not.toBeNull()
      return element!
    })

    pages.at(-1)!.style.paddingBottom = '400px'
    wheel(120)
    await new Promise((resolve) => setTimeout(resolve, 100))
    await expect(hint).not.toHaveAttribute('data-leaving')

    scroller.scrollTop = scroller.scrollHeight
    await waitFor(() => expect(scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight).toBeLessThan(2))
    for (let i = 0; i < 4; i++) wheel(120)
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

// SwiftShader runs the shaders on the CPU when the runner has no GPU, which can take a while.
const ENGINE_TIMEOUT = 60_000

async function engineWorks(): Promise<boolean> {
  const { loadEnhanceEngine } = await import('../../lib/enhance')
  const engine = await loadEnhanceEngine()
  return engine.supported && !engine.broken
}

const compareKey = (type: 'keydown' | 'keyup') => window.dispatchEvent(new KeyboardEvent(type, { key: 'h' }))

/**
 * Only the enhance range is enhanced, each canvas drawn over its page image, which stays in the
 * strip's flow and painted underneath (hidden from assistive tech only). Holding H shows the originals.
 */
export const Enhanced: Story = {
  args: { enhance: 'always', enhanceMethod: 'a4k-s' },
  play: async ({ args, canvas, canvasElement }) => {
    if (!(await engineWorks())) {
      await expect(await canvas.findByRole('img', { name: 'Chapter 40 - page 1' })).toBeVisible()
      return
    }
    const original = await waitFor(() => {
      const element = canvasElement.querySelector<HTMLImageElement>('img[data-page="0"]')
      expect(element).toHaveAttribute('data-loaded')
      return element!
    })
    const enhanced = await waitFor(
      () => {
        const element = original.nextElementSibling as HTMLCanvasElement | null
        expect(element?.dataset.enhanced).toBe('true')
        return element!
      },
      { timeout: ENGINE_TIMEOUT },
    )
    await expect(enhanced).toHaveAccessibleName('Chapter 40 - page 1')
    await waitFor(() => expect(original).toHaveAttribute('aria-hidden', 'true'))
    await expect(original).toBeVisible()
    const a = original.getBoundingClientRect()
    const b = enhanced.getBoundingClientRect()
    await expect(Math.abs(a.top - b.top) + Math.abs(a.left - b.left) + Math.abs(a.width - b.width)).toBeLessThan(2)
    const range = (args.onEnhanceRange as Mock<(pages: number[], beyond: number, server: number[]) => void>).mock.lastCall?.[0] ?? []
    await expect(canvasElement.querySelectorAll('canvas.reader-enhanced').length).toBeLessThanOrEqual(range.length)

    compareKey('keydown')
    await waitFor(() => expect(enhanced).not.toBeVisible())
    await waitFor(() => expect(original).not.toHaveAttribute('aria-hidden'))

    compareKey('keyup')
    await waitFor(() => expect(enhanced).toBeVisible())
    await waitFor(() => expect(original).toHaveAttribute('aria-hidden', 'true'))
  },
}

/**
 * Pages shown small run the enhance range far down the strip, here to the end of the chapter, with
 * budget left over for the next chapter's first pages.
 */
export const EnhanceRangeRunsFarOnShortPages: Story = {
  args: { enhance: 'always', enhanceMethod: 'a4k-s', fit: 'original', scale: 25 },
  play: async ({ args }) => {
    const lastCall = () => (args.onEnhanceRange as Mock<(pages: number[], beyond: number, server: number[]) => void>).mock.lastCall
    await waitFor(() => expect(lastCall()?.[0]).toEqual(Array.from({ length: 12 }, (_, i) => i)))
    await waitFor(() => expect(lastCall()?.[1]).toBeGreaterThan(0))
  },
}

/** Pages several screens tall keep the range to the current page and the next one. */
export const EnhanceRangeStaysShortOnTallPages: Story = {
  args: { enhance: 'always', enhanceMethod: 'a4k-s', fit: 'original', scale: 400 },
  play: async ({ args }) => {
    await waitFor(() => expect(args.onEnhanceRange).toHaveBeenLastCalledWith([0, 1], 0, [0, 1]))
  },
}

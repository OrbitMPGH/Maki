import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, spreadImage } from '../../../.storybook/mocks/api'
import { pageUrlsFor } from '../../../.storybook/mocks/entry/fixtures'
import { toneFilter } from './prefs'
import PagedView from './PagedView'

const meta = {
  component: PagedView,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen' },
  args: {
    urls: pageUrlsFor(1132, 18),
    spread: [5],
    fit: 'height',
    direction: 'rtl',
    zoom: 1,
    scale: 100,
    label: 'Chapter 132',
    onMeasure: fn(),
  },
  decorators: [
    (Story) => (
      <div className="reader-root">
        <div className="reader-surface">
          <Story />
        </div>
      </div>
    ),
  ],
} satisfies Meta<typeof PagedView>

export default meta
type Story = StoryObj<typeof meta>

export const SinglePage: Story = {
  play: async ({ args, canvas }) => {
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible()
    await waitFor(() => expect(args.onMeasure).toHaveBeenCalledWith(5, expect.anything()))
  },
}

export const FitWidth: Story = {
  args: { fit: 'width' },
}

export const OriginalSizeScaledDown: Story = {
  args: { fit: 'original', scale: 60 },
}

export const DoublePageRightToLeft: Story = {
  args: { spread: [3, 4] },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 4' })
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 5' })).toBeVisible()
    // Right to left puts the lower page number on the right.
    const [first, second] = Array.from(canvasElement.querySelectorAll('img.reader-page')).map((img) => img.getAttribute('alt'))
    await expect(first).toBe('Chapter 132 - page 5')
    await expect(second).toBe('Chapter 132 - page 4')
  },
}

export const DoublePageLeftToRight: Story = {
  args: { spread: [3, 4], direction: 'ltr' },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 4' })
    const [first] = Array.from(canvasElement.querySelectorAll('img.reader-page')).map((img) => img.getAttribute('alt'))
    await expect(first).toBe('Chapter 132 - page 4')
  },
}

export const ZoomedIn: Story = {
  args: { zoom: 2 },
}

export const SepiaAndDimmed: Story = {
  args: { tone: toneFilter({ dim: 30, filter: 'sepia' }) },
}

export const PageFailsToLoad: Story = {
  args: { urls: pageUrlsFor(4404, 10), spread: [0], label: 'Chapter 61' },
  parameters: {
    msw: [http.get(api('/reader/chapter/4404/page/:n'), () => new HttpResponse(null, { status: 500 }))],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('This page failed to load.')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Try again' })).toBeVisible()
  },
}

const spreads = Array.from({ length: 6 }, (_, i) => spreadImage(77, i + 1))

/** Right to left reads a spread's right half first, so that is the half on screen. */
export const SplitWidePageRightToLeft: Story = {
  args: { urls: spreads, spread: [2], splitWide: 'split', half: 0, label: 'Chapter 7' },
  play: async ({ canvas, canvasElement }) => {
    const page = await canvas.findByRole('img', { name: 'Chapter 7 - page 3' })
    await waitFor(() => expect(page).toBeVisible())
    await waitFor(() => expect(page).toHaveClass('reader-region-image'))
    await expect(page.style.left).toBe('-100%')
    await expect(canvasElement.querySelector('canvas')?.width).toBe(800)
  },
}

export const SplitWidePageSecondHalf: Story = {
  args: { urls: spreads, spread: [2], splitWide: 'split', half: 1, label: 'Chapter 7' },
  play: async ({ canvas }) => {
    const page = await canvas.findByRole('img', { name: 'Chapter 7 - page 3' })
    await expect(page.style.left).toBe('0%')
  },
}

export const SplitWidePageLeftToRight: Story = {
  args: { urls: spreads, spread: [2], splitWide: 'split', half: 0, direction: 'ltr', label: 'Chapter 7' },
  play: async ({ canvas }) => {
    const page = await canvas.findByRole('img', { name: 'Chapter 7 - page 3' })
    await expect(page.style.left).toBe('0%')
  },
}

/** Turned anticlockwise in right to left, so the half read first ends up on top. */
export const RotatedWidePage: Story = {
  args: { urls: spreads, spread: [2], splitWide: 'rotate', label: 'Chapter 7' },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 7 - page 3' })
    const sizer = canvasElement.querySelector('canvas')
    await expect(sizer?.width).toBe(1200)
    await expect(sizer?.height).toBe(1600)
    await expect(canvasElement.querySelector<HTMLElement>('.reader-region-frame')?.style.transform).toContain('rotate(-90deg)')
  },
}

/** A portrait page is never split, whatever the setting says. */
export const SplitLeavesPortraitPagesWhole: Story = {
  args: { splitWide: 'split' },
  play: async ({ canvas, canvasElement }) => {
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 6' })).toHaveClass('reader-page'))
    await expect(canvasElement.querySelector('.reader-region')).toBeNull()
  },
}

/** The generated pages sit on a 30px white margin, which the crop trims. */
export const CroppedBorders: Story = {
  args: { cropBorders: true },
  play: async ({ canvas, canvasElement }) => {
    await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible()
    const sizer = canvasElement.querySelector('canvas')
    await expect(sizer).not.toBeNull()
    await expect(sizer!.width).toBeLessThan(800)
    await expect(sizer!.height).toBeLessThan(1200)
  },
}

export const CroppedBordersAtOriginalSize: Story = {
  args: { cropBorders: true, fit: 'original' },
}

export const CroppedDoublePage: Story = {
  args: { spread: [3, 4], cropBorders: true },
  play: async ({ canvas, canvasElement }) => {
    await canvas.findByRole('img', { name: 'Chapter 132 - page 4' })
    await canvas.findByRole('img', { name: 'Chapter 132 - page 5' })
    await waitFor(() => expect(canvasElement.querySelectorAll('.reader-region')).toHaveLength(2))
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

/** Enhance Always: the page is drawn by the engine, and holding H shows the original until released. */
export const Enhanced: Story = {
  args: { enhance: 'always', enhanceMethod: 'a4k-s' },
  play: async ({ canvas, canvasElement }) => {
    if (!(await engineWorks())) {
      await expect(await canvas.findByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible()
      return
    }
    const enhanced = await waitFor(
      () => {
        const element = canvasElement.querySelector<HTMLCanvasElement>('canvas.reader-enhanced')
        expect(element?.dataset.enhanced).toBe('true')
        return element!
      },
      { timeout: ENGINE_TIMEOUT },
    )
    const original = canvasElement.querySelector<HTMLImageElement>('img.reader-region-image')!
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 6' })).toBe(enhanced))
    await waitFor(() => expect(original).not.toBeVisible())
    await expect(enhanced.width).toBeGreaterThan(0)

    compareKey('keydown')
    await waitFor(() => expect(original).toBeVisible())
    await expect(enhanced).not.toBeVisible()
    await expect(canvas.getByRole('img', { name: 'Chapter 132 - page 6' })).toBe(original)

    compareKey('keyup')
    await waitFor(() => expect(enhanced).toBeVisible())
    await waitFor(() => expect(original).not.toBeVisible())
  },
}

/** A split half goes through the same canvas, enhanced from just that half. */
export const EnhancedSplitHalf: Story = {
  args: { urls: spreads, spread: [2], splitWide: 'split', half: 0, label: 'Chapter 7', enhance: 'always', enhanceMethod: 'a4k-s' },
  play: async ({ canvasElement }) => {
    if (!(await engineWorks())) return
    await waitFor(
      () => expect(canvasElement.querySelector<HTMLCanvasElement>('canvas.reader-enhanced')?.dataset.enhanced).toBe('true'),
      { timeout: ENGINE_TIMEOUT },
    )
    await expect(canvasElement.querySelector<HTMLElement>('img.reader-region-image')?.style.left).toBe('-100%')
  },
}

/** Auto leaves a page alone when it is shown smaller than its source (1200px tall in an 800px view). */
export const EnhanceAutoAtSourceSize: Story = {
  args: { enhance: 'auto', enhanceMethod: 'a4k-s' },
  play: async ({ canvas, canvasElement }) => {
    if (!(await engineWorks())) return
    await waitFor(() => expect(canvasElement.querySelector('canvas.reader-enhanced')).not.toBeNull())
    await new Promise((resolve) => setTimeout(resolve, 500))
    await expect(canvasElement.querySelector<HTMLCanvasElement>('canvas.reader-enhanced')?.dataset.enhanced).toBe('false')
    await expect(canvas.getByRole('img', { name: 'Chapter 132 - page 6' })).toHaveClass('reader-region-image')
  },
}

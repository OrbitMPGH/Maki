import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api } from '../../../.storybook/mocks/api'
import { chapters } from '../../../.storybook/mocks/entry/fixtures'
import type { ReaderManifest } from '../../api/reader'
import ReaderPage from './ReaderPage'

const meta = {
  component: ReaderPage,
  tags: ['ai-generated'],
  parameters: {
    layout: 'fullscreen',
    router: { path: '/read/:chapterId', initialEntries: ['/read/1132'] },
  },
} satisfies Meta<typeof ReaderPage>

export default meta
type Story = StoryObj<typeof meta>

type Canvas = Parameters<NonNullable<Story['play']>>[0]['canvas']

const withManifest = (manifest: ReaderManifest) => http.get(api('/reader/chapter/:id'), () => HttpResponse.json(manifest))

const pause = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

interface Point {
  x: number
  y: number
}

function fire(target: HTMLElement, type: string, id: number, point: Point, pointerType = 'touch') {
  target.dispatchEvent(
    new PointerEvent(type, {
      bubbles: true,
      cancelable: true,
      composed: true,
      pointerId: id,
      pointerType,
      isPrimary: id === 1,
      button: 0,
      buttons: type === 'pointerup' ? 0 : 1,
      clientX: point.x,
      clientY: point.y,
    }),
  )
}

const lerp = (a: Point, b: Point, f: number) => ({ x: a.x + (b.x - a.x) * f, y: a.y + (b.y - a.y) * f })

/** One finger from `from` to `to`, then the click a browser may send after it. */
function drag(surface: HTMLElement, from: Point, to: Point, pointerType = 'touch') {
  fire(surface, 'pointerdown', 1, from, pointerType)
  for (let step = 1; step <= 6; step++) fire(surface, 'pointermove', 1, lerp(from, to, step / 6), pointerType)
  fire(surface, 'pointerup', 1, to, pointerType)
  surface.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: to.x, clientY: to.y }))
}

function pinch(surface: HTMLElement, centre: Point, from: number, to: number) {
  const at = (spread: number) => [
    { x: centre.x - spread, y: centre.y },
    { x: centre.x + spread, y: centre.y },
  ]
  const [a, b] = at(from)
  fire(surface, 'pointerdown', 1, a)
  fire(surface, 'pointerdown', 2, b)
  for (let step = 1; step <= 8; step++) {
    const [c, d] = at(from + ((to - from) * step) / 8)
    fire(surface, 'pointermove', 1, c)
    fire(surface, 'pointermove', 2, d)
  }
  const [c, d] = at(to)
  fire(surface, 'pointerup', 2, d)
  fire(surface, 'pointerup', 1, c)
}

function wheel(surface: HTMLElement, deltaY: number) {
  surface.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY, deltaMode: 0 }))
}

async function readySurface(canvas: Canvas, canvasElement: HTMLElement, page: number) {
  await canvas.findByRole('img', { name: `Chapter 132 - page ${page}` })
  const surface = canvasElement.querySelector<HTMLElement>('.reader-surface')
  if (!surface) throw new Error('The reader surface did not render')
  await waitFor(() => expect(surface).toHaveAttribute('data-gestures', 'paged'))
  const box = surface.getBoundingClientRect()
  return { surface, centre: { x: box.left + box.width / 2, y: box.top + box.height / 2 }, box }
}

/** Right to left: a swipe to the right moves on, to the left goes back. A steep drag does neither, and no swipe also taps. */
export const SwipeTurnsThePage: Story = {
  play: async ({ canvas, canvasElement }) => {
    const { surface, centre } = await readySurface(canvas, canvasElement, 6)
    drag(surface, { x: centre.x - 80, y: centre.y }, { x: centre.x + 80, y: centre.y + 12 })
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 7' })).toBeVisible())
    await expect(canvasElement.querySelector('.reader-bar-bottom')).toHaveAttribute('data-visible', 'false')

    drag(surface, { x: centre.x, y: centre.y - 120 }, { x: centre.x + 70, y: centre.y + 120 })
    await pause(300)
    await expect(canvas.queryByRole('img', { name: 'Chapter 132 - page 8' })).toBeNull()
    await expect(canvasElement.querySelector('.reader-bar-bottom')).toHaveAttribute('data-visible', 'false')

    drag(surface, { x: centre.x + 80, y: centre.y }, { x: centre.x - 80, y: centre.y })
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible())
  },
}

/**
 * A trackpad flick is a long burst of shrinking deltas and turns one page, however long its inertia
 * runs. A wheel notch after a pause turns the next, and wheeling up goes back.
 */
export const WheelTurnsOncePerGesture: Story = {
  play: async ({ canvas, canvasElement }) => {
    const { surface } = await readySurface(canvas, canvasElement, 6)
    for (let i = 0; i < 50; i++) {
      wheel(surface, Math.max(1, Math.round(30 * Math.pow(0.92, i))))
      await pause(12)
    }
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 7' })).toBeVisible())
    await pause(300)
    await expect(canvas.queryByRole('img', { name: 'Chapter 132 - page 8' })).toBeNull()

    wheel(surface, 100)
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 8' })).toBeVisible())
    await pause(300)
    for (let i = 0; i < 30; i++) {
      wheel(surface, -4)
      await pause(10)
    }
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 7' })).toBeVisible())
    await pause(300)
    await expect(canvas.queryByRole('img', { name: 'Chapter 132 - page 6' })).toBeNull()
  },
}

/**
 * Two fingers zoom the reader itself, not the browser. Zoomed, one finger pans and never turns the
 * page, and a double tap goes back to fit.
 */
export const PinchZoomsAndPans: Story = {
  play: async ({ canvas, canvasElement }) => {
    const { surface, centre } = await readySurface(canvas, canvasElement, 6)
    const content = canvasElement.querySelector<HTMLElement>('.reader-paged')!
    pinch(surface, centre, 40, 100)
    await waitFor(() => expect(content.style.transform).toBe('scale(2.5)'))
    await waitFor(() => expect(surface).toHaveAttribute('data-zoomed', 'true'))
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible())

    const left = surface.scrollLeft
    drag(surface, { x: centre.x + 60, y: centre.y }, { x: centre.x - 60, y: centre.y })
    await waitFor(() => expect(surface.scrollLeft).toBeGreaterThan(left + 60))
    await pause(300)
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible())

    for (let tap = 0; tap < 2; tap++) {
      fire(surface, 'pointerdown', 1, centre)
      fire(surface, 'pointerup', 1, centre)
      surface.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, clientX: centre.x, clientY: centre.y }))
    }
    await waitFor(() => expect(content.style.transform).toBe(''))
    await waitFor(() => expect(surface).not.toHaveAttribute('data-zoomed'))
  },
}

/** Kindle, left to right: most of the page goes forward, a strip on the left goes back, the top opens the bars. */
export const KindleTapZones: Story = {
  parameters: {
    msw: [
      withManifest({
        ...chapters[1132],
        prefs: { ...chapters[1132].prefs, direction: 'ltr', tapLayout: 'kindle', tapWidth: 25 },
      }),
    ],
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    const { surface, box } = await readySurface(canvas, canvasElement, 6)
    const at = (x: number, y: number) => ({ clientX: box.left + box.width * x, clientY: box.top + box.height * y })
    await userEvent.pointer({ keys: '[MouseLeft]', target: surface, coords: at(0.6, 0.6) })
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 7' })).toBeVisible())
    await userEvent.pointer({ keys: '[MouseLeft]', target: surface, coords: at(0.1, 0.6) })
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 6' })).toBeVisible())

    // A mouse drag that ends in a forward zone is not a click there.
    drag(surface, { x: box.left + box.width * 0.4, y: box.top + box.height * 0.6 }, { x: box.left + box.width * 0.7, y: box.top + box.height * 0.6 }, 'mouse')
    await pause(300)
    await expect(canvas.queryByRole('img', { name: 'Chapter 132 - page 7' })).toBeNull()

    await userEvent.pointer({ keys: '[MouseLeft]', target: surface, coords: at(0.5, 0.1) })
    await waitFor(() => expect(canvasElement.querySelector('.reader-bar-bottom')).toHaveAttribute('data-visible', 'true'))
  },
}

/**
 * Fit width on a page taller than the screen: a finger drag scrolls it, and the wheel scrolls before
 * it turns. A gesture that scrolled to the bottom stops there; the next one turns the page.
 */
export const FitWidthScrollsFirst: Story = {
  parameters: {
    msw: [withManifest({ ...chapters[1132], prefs: { ...chapters[1132].prefs, fit: 'width' } })],
  },
  play: async ({ canvas, canvasElement }) => {
    const { surface, centre } = await readySurface(canvas, canvasElement, 6)
    await waitFor(() => expect(surface.scrollHeight).toBeGreaterThan(surface.clientHeight * 1.5))
    drag(surface, { x: centre.x, y: centre.y + 100 }, { x: centre.x, y: centre.y - 100 })
    await waitFor(() => expect(surface.scrollTop).toBeGreaterThan(150))

    wheel(surface, 100)
    surface.scrollTop = surface.scrollHeight
    wheel(surface, 100)
    await pause(300)
    await expect(canvas.queryByRole('img', { name: 'Chapter 132 - page 7' })).toBeNull()

    wheel(surface, 100)
    await waitFor(() => expect(canvas.getByRole('img', { name: 'Chapter 132 - page 7' })).toBeVisible())
    await waitFor(() => expect(surface.scrollTop).toBe(0))
  },
}

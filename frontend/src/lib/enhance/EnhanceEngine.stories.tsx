import type { Meta, StoryObj } from '@storybook/react-vite'
import { useEffect, useRef, useState } from 'react'
import { expect, waitFor } from 'storybook/test'
import { EnhanceError, loadEnhanceEngine, type EnhancePreset } from './index'
import { spans, tileLimit } from './tiles'

interface DemoProps {
  preset: EnhancePreset
  width: number
  height: number
  targetWidth: number
  targetHeight: number
  /** Also enhances a crop around the first tile boundary without tiling, for the seam check. */
  seamCheck?: boolean
}

function pageSvg(w: number, h: number): string {
  const lines: string[] = []
  for (let y = 40; y < h - 40; y += 160) {
    lines.push(`<rect x="16" y="${y}" width="${w - 32}" height="140" fill="url(#tone)" stroke="#111" stroke-width="3"/>`)
    lines.push(`<ellipse cx="${w / 2}" cy="${y + 70}" rx="${w / 3}" ry="44" fill="#fff" stroke="#111" stroke-width="2"/>`)
    lines.push(`<text x="${w / 2}" y="${y + 80}" font-family="sans-serif" font-size="26" font-weight="700" text-anchor="middle" fill="#111">MAKI ${y}</text>`)
    lines.push(`<line x1="20" y1="${y + 4}" x2="${w - 20}" y2="${y + 136}" stroke="#222" stroke-width="1.5"/>`)
  }
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">
<defs><pattern id="tone" width="6" height="6" patternUnits="userSpaceOnUse"><rect width="6" height="6" fill="#f4f4f4"/><circle cx="3" cy="3" r="1.6" fill="#333"/></pattern></defs>
<rect width="${w}" height="${h}" fill="#fafafa"/>${lines.join('')}</svg>`
}

async function pageBitmap(w: number, h: number): Promise<{ bitmap: ImageBitmap; url: string }> {
  const url = `data:image/svg+xml;charset=utf-8,${encodeURIComponent(pageSvg(w, h))}`
  const img = new Image(w, h)
  img.src = url
  await img.decode()
  const canvas = document.createElement('canvas')
  canvas.width = w
  canvas.height = h
  canvas.getContext('2d')?.drawImage(img, 0, 0, w, h)
  return { bitmap: await createImageBitmap(canvas), url }
}

function maxTextureSize(): number {
  const gl = document.createElement('canvas').getContext('webgl2')
  return gl ? (gl.getParameter(gl.MAX_TEXTURE_SIZE) as number) : 0
}

function draw(canvas: HTMLCanvasElement | null, bitmap: ImageBitmap) {
  if (!canvas) return
  canvas.width = bitmap.width
  canvas.height = bitmap.height
  canvas.getContext('2d')?.drawImage(bitmap, 0, 0)
}

function EnhanceDemo({ preset, width, height, targetWidth, targetHeight, seamCheck }: DemoProps) {
  const out = useRef<HTMLCanvasElement>(null)
  const crop = useRef<HTMLCanvasElement>(null)
  const [state, setState] = useState<{ status: string; url?: string; boundary?: number; tiles?: number }>({ status: 'loading' })

  useEffect(() => {
    let live = true
    const controller = new AbortController()
    void (async () => {
      const engine = await loadEnhanceEngine()
      const { bitmap, url } = await pageBitmap(width, height)
      try {
        const result = await engine.enhance(bitmap, { preset, targetWidth, targetHeight, signal: controller.signal })
        if (!live) return result.close()
        draw(out.current, result)
        result.close()
        let boundary: number | undefined
        let tiles: number | undefined
        if (seamCheck) {
          const ys = spans(height, tileLimit(maxTextureSize()))
          tiles = ys.length
          boundary = ys[0].inEnd
          const top = boundary - 128
          const again = (await pageBitmap(width, height)).bitmap
          const source = await createImageBitmap(again, 0, top, width, 256)
          again.close()
          const part = await engine.enhance(source, { preset, targetWidth: width * 2, targetHeight: 512, signal: controller.signal })
          if (!live) return part.close()
          draw(crop.current, part)
          part.close()
        }
        setState({ status: 'done', url, boundary, tiles })
      } catch (error) {
        if (!live) return
        setState({ status: error instanceof EnhanceError ? error.code : 'error', url })
      }
    })()
    return () => {
      live = false
      controller.abort()
    }
  }, [preset, width, height, targetWidth, targetHeight, seamCheck])

  const plain = state.status === 'unsupported' || state.status === 'broken'
  return (
    <div data-state={state.status} data-boundary={state.boundary} data-tiles={state.tiles}>
      {plain && (
        <>
          <p>Not supported on this device</p>
          <img src={state.url} alt="Mock page" width={width} height={height} />
        </>
      )}
      <canvas ref={out} role="img" aria-label="Enhanced page" hidden={plain} style={{ maxWidth: '100%' }} />
      {seamCheck && <canvas ref={crop} role="img" aria-label="Boundary crop" hidden={plain} style={{ maxWidth: '100%' }} />}
    </div>
  )
}

const meta = {
  component: EnhanceDemo,
  tags: ['ai-generated'],
  args: { preset: 'a4k-m', width: 360, height: 520, targetWidth: 540, targetHeight: 780 },
} satisfies Meta<typeof EnhanceDemo>

export default meta
type Story = StoryObj<typeof meta>

// SwiftShader compiles and runs the CNN on the CPU when the runner has no GPU, which can take a while.
const ENGINE_TIMEOUT = 60_000

async function settled(canvasElement: HTMLElement): Promise<HTMLElement> {
  const root = canvasElement.querySelector<HTMLElement>('[data-state]')
  if (!root) throw new Error('demo did not render')
  await waitFor(() => expect(root.dataset.state).not.toBe('loading'), { timeout: ENGINE_TIMEOUT })
  return root
}

function pixels(canvas: HTMLCanvasElement): ImageData {
  const ctx = canvas.getContext('2d')
  if (!ctx) throw new Error('no 2d context')
  return ctx.getImageData(0, 0, canvas.width, canvas.height)
}

async function expectFallback(root: HTMLElement) {
  await expect(['unsupported', 'broken']).toContain(root.dataset.state)
  const img = root.querySelector('img')
  await expect(img).not.toBeNull()
  await expect(img).toBeVisible()
}

async function expectEnhanced(root: HTMLElement, w: number, h: number) {
  await expect(root.dataset.state).toBe('done')
  const canvas = root.querySelector('canvas')
  if (!canvas) throw new Error('no canvas')
  await expect([canvas.width, canvas.height]).toEqual([w, h])
  const { data } = pixels(canvas)
  let lo = 255
  let hi = 0
  for (let j = 1; j < 16; j++) {
    for (let i = 1; i < 16; i++) {
      const o = (Math.floor((j * h) / 16) * w + Math.floor((i * w) / 16)) * 4
      const luma = (data[o] + data[o + 1] + data[o + 2]) / 3
      lo = Math.min(lo, luma)
      hi = Math.max(hi, luma)
      await expect(data[o + 3]).toBe(255)
    }
  }
  await expect(lo).toBeLessThan(100)
  await expect(hi).toBeGreaterThan(200)
}

function playFor(w: number, h: number): Story['play'] {
  return async ({ canvasElement }) => {
    const root = await settled(canvasElement)
    if (root.dataset.state === 'done') await expectEnhanced(root, w, h)
    else await expectFallback(root)
  }
}

export const Light: Story = { args: { preset: 'a4k-s' }, play: playFor(540, 780) }

export const Balanced: Story = { args: { preset: 'a4k-m' }, play: playFor(540, 780) }

export const Strong: Story = { args: { preset: 'a4k-a' }, play: playFor(540, 780) }

/** A target above 2x is capped at 2x source. */
export const CappedAtTwice: Story = { args: { preset: 'a4k-s', targetWidth: 2000, targetHeight: 3000 }, play: playFor(720, 1040) }

const rowDiff = (d: ImageData, a: number, b: number) => {
  let sum = 0
  for (let x = 0; x < d.width; x++) for (let c = 0; c < 3; c++) sum += Math.abs(d.data[(a * d.width + x) * 4 + c] - d.data[(b * d.width + x) * 4 + c])
  return sum / (d.width * 3)
}

/** Taller than the tile limit, so it runs in two tiles. The boundary must not show. */
export const TiledTallPage: Story = {
  args: { preset: 'a4k-a', width: 256, height: 2300, targetWidth: 512, targetHeight: 4600, seamCheck: true },
  play: async ({ canvasElement }) => {
    const root = await settled(canvasElement)
    if (root.dataset.state !== 'done') return expectFallback(root)
    await expectEnhanced(root, 512, 4600)
    await expect(Number(root.dataset.tiles)).toBeGreaterThan(1)
    const [full, crop] = Array.from(root.querySelectorAll('canvas'))
    const seam = Number(root.dataset.boundary) * 2
    const tiled = pixels(full)

    const around: number[] = []
    for (let r = seam - 8; r <= seam + 8; r++) if (r !== seam) around.push(rowDiff(tiled, r, r - 1))
    around.sort((a, b) => a - b)
    const typical = around[around.length >> 1]
    await expect(rowDiff(tiled, seam, seam - 1)).toBeLessThanOrEqual(typical * 2 + 4)

    // The same rows enhanced from an untiled crop: the tiled output must match it across the boundary.
    const untiled = pixels(crop)
    const offset = seam - 256
    let worst = 0
    let total = 0
    let n = 0
    for (let r = seam - 24; r < seam + 24; r++) {
      for (let x = 0; x < tiled.width; x++) {
        for (let c = 0; c < 3; c++) {
          const d = Math.abs(tiled.data[(r * tiled.width + x) * 4 + c] - untiled.data[((r - offset) * untiled.width + x) * 4 + c])
          worst = Math.max(worst, d)
          total += d
          n++
        }
      }
    }
    await expect(worst).toBeLessThanOrEqual(8)
    await expect(total / n).toBeLessThan(1)
  },
}

import { HookRunner, setSampling, type Size, type Tex } from './mpvHook'
import { PRESET_FILES, type EnhancePreset } from './presets'
import { spans, tileLimit } from './tiles'

type AnyCanvas = HTMLCanvasElement | OffscreenCanvas

export type ProbeResult = { ok: true; gl: WebGL2RenderingContext } | { ok: false; reason: string }

export function probe(canvas: AnyCanvas, preserveDrawingBuffer: boolean): ProbeResult {
  const opts: WebGLContextAttributes = {
    alpha: false,
    antialias: false,
    depth: false,
    stencil: false,
    premultipliedAlpha: false,
    preserveDrawingBuffer,
    powerPreference: 'high-performance',
  }
  let gl: WebGL2RenderingContext | null = null
  try {
    gl = canvas.getContext('webgl2', opts) as WebGL2RenderingContext | null
  } catch {
    gl = null
  }
  if (!gl) return { ok: false, reason: 'no-webgl2' }
  if (!gl.getExtension('EXT_color_buffer_float')) return { ok: false, reason: 'no-color-buffer-float' }
  return { ok: true, gl }
}

export class EnhanceRenderer {
  private readonly gl: WebGL2RenderingContext
  private readonly canvas: AnyCanvas
  private readonly runner: HookRunner
  private readonly src: Tex
  readonly maxTextureSize: number
  private readonly maxOutput: number

  constructor(canvas: AnyCanvas, gl: WebGL2RenderingContext) {
    this.canvas = canvas
    this.gl = gl
    this.runner = new HookRunner(gl)
    this.maxTextureSize = gl.getParameter(gl.MAX_TEXTURE_SIZE) as number
    const viewport = gl.getParameter(gl.MAX_VIEWPORT_DIMS) as Int32Array
    this.maxOutput = Math.min(
      this.maxTextureSize,
      gl.getParameter(gl.MAX_RENDERBUFFER_SIZE) as number,
      viewport[0],
      viewport[1],
    )
    const tex = gl.createTexture()
    gl.bindTexture(gl.TEXTURE_2D, tex)
    setSampling(gl)
    this.src = { tex, fb: gl.createFramebuffer(), w: 0, h: 0 }
    gl.pixelStorei(gl.UNPACK_COLORSPACE_CONVERSION_WEBGL, gl.NONE)
    gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false)
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false)
  }

  /** Output size for a request: the target, capped at 2x source per axis and at what the GPU can draw. */
  outputSize(source: Size, targetW: number, targetH: number): Size {
    let w = Math.max(1, Math.min(Math.round(targetW), source.w * 2))
    let h = Math.max(1, Math.min(Math.round(targetH), source.h * 2))
    const s = Math.min(1, this.maxOutput / w, this.maxOutput / h)
    if (s < 1) {
      w = Math.max(1, Math.floor(w * s))
      h = Math.max(1, Math.floor(h * s))
    }
    return { w, h }
  }

  /**
   * Draws `source` through `preset` into the canvas's drawing buffer at the output size and returns
   * that size. The caller snapshots the canvas. Throws on any GL error; the engine is then broken.
   */
  async render(source: ImageBitmap, preset: EnhancePreset, targetW: number, targetH: number): Promise<Size> {
    const gl = this.gl
    if (gl.isContextLost()) throw new Error('context lost')
    const files = PRESET_FILES[preset] as string[]
    this.runner.prepare(files)
    this.runner.prepareResample()

    const sw = source.width
    const sh = source.height
    let { w: dw, h: dh } = this.outputSize({ w: sw, h: sh }, targetW, targetH)
    this.canvas.width = dw
    this.canvas.height = dh
    if (gl.drawingBufferWidth !== dw || gl.drawingBufferHeight !== dh) {
      const s = Math.min(gl.drawingBufferWidth / dw, gl.drawingBufferHeight / dh)
      dw = Math.max(1, Math.floor(dw * s))
      dh = Math.max(1, Math.floor(dh * s))
      this.canvas.width = dw
      this.canvas.height = dh
    }

    const limit = tileLimit(this.maxTextureSize)
    const xs = spans(sw, limit)
    const ys = spans(sh, limit)
    const single = xs.length === 1 && ys.length === 1
    // Tiles are cut with a 2D drawImage, not createImageBitmap's crop: some browsers upload a
    // cropped bitmap's whole source, which draws the full page once per tile row.
    const cut = single ? null : this.tileCanvas()

    try {
      ys.forEach((y) => {
        xs.forEach((x) => {
          const tw = x.end - x.start
          const th = y.end - y.start
          let tile: TexImageSource = source
          if (cut) {
            cut.canvas.width = tw
            cut.canvas.height = th
            cut.ctx.drawImage(source, x.start, y.start, tw, th, 0, 0, tw, th)
            tile = cut.canvas
          }
          gl.bindTexture(gl.TEXTURE_2D, this.src.tex)
          gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, gl.RGBA, gl.UNSIGNED_BYTE, tile)
          this.src.w = tw
          this.src.h = th
          const main = this.runner.runFiles(this.src, files, { w: tw * 2, h: th * 2 })
          const dx0 = Math.round((x.inStart * dw) / sw)
          const dx1 = Math.round((x.inEnd * dw) / sw)
          const dy0 = Math.round((y.inStart * dh) / sh)
          const dy1 = Math.round((y.inEnd * dh) / sh)
          gl.enable(gl.SCISSOR_TEST)
          gl.scissor(dx0, dh - dy1, dx1 - dx0, dy1 - dy0)
          this.runner.resample(main, this.src, dw, dh, [sw / dw / tw, sh / dh / th], [-x.start / tw, -y.start / th])
          gl.disable(gl.SCISSOR_TEST)
          this.runner.releaseTex(main)
        })
      })
      this.runner.trim()
      const err = gl.getError()
      if (err !== gl.NO_ERROR) throw new Error(err === gl.OUT_OF_MEMORY ? 'out of memory' : `gl error ${err}`)
    } finally {
      if (cut) {
        cut.canvas.width = 0
        cut.canvas.height = 0
      }
    }
    return { w: dw, h: dh }
  }

  private tileCanvas(): { canvas: AnyCanvas; ctx: OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D } {
    const canvas = typeof OffscreenCanvas === 'undefined' ? document.createElement('canvas') : new OffscreenCanvas(1, 1)
    const ctx = canvas.getContext('2d') as OffscreenCanvasRenderingContext2D | CanvasRenderingContext2D | null
    if (!ctx) throw new Error('no 2d context for tiles')
    return { canvas, ctx }
  }

  dispose() {
    this.runner.dispose()
    this.gl.deleteTexture(this.src.tex)
    this.gl.deleteFramebuffer(this.src.fb)
  }
}

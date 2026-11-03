import type { JobMessage, WorkerMessage } from './protocol'
import { EnhanceRenderer, probe } from './renderer'

interface WorkerScope {
  postMessage(message: WorkerMessage, options?: StructuredSerializeOptions): void
  addEventListener(type: 'message', listener: (event: MessageEvent<JobMessage>) => void): void
}

const scope = self as unknown as WorkerScope
const post = (message: WorkerMessage, transfer: Transferable[] = []) => scope.postMessage(message, { transfer })

let renderer: EnhanceRenderer | null = null
let lost = false

const canvas = typeof OffscreenCanvas === 'undefined' ? null : new OffscreenCanvas(1, 1)
const probed = canvas ? probe(canvas, false) : { ok: false as const, reason: 'no-offscreen-canvas' }

if (canvas && probed.ok) {
  canvas.addEventListener('webglcontextlost', (event) => {
    event.preventDefault()
    lost = true
    post({ type: 'lost' })
  })
  try {
    renderer = new EnhanceRenderer(canvas, probed.gl)
    post({ type: 'ready', supported: true })
  } catch (error) {
    post({ type: 'ready', supported: false, reason: String(error) })
  }
} else {
  post({ type: 'ready', supported: false, reason: probed.ok ? 'no-offscreen-canvas' : probed.reason })
}

scope.addEventListener('message', async ({ data }) => {
  if (data.type !== 'job') return
  try {
    if (!renderer || !canvas || lost) throw new Error('context lost')
    await renderer.render(data.bitmap, data.preset, data.width, data.height)
    const bitmap = canvas.transferToImageBitmap()
    post({ type: 'done', id: data.id, bitmap }, [bitmap])
  } catch (error) {
    post({ type: 'failed', id: data.id, message: error instanceof Error ? error.message : String(error) })
  } finally {
    data.bitmap.close()
  }
})

import type { Backend } from './protocol'
import { EnhanceRenderer, probe } from './renderer'

/** The same renderer on a detached canvas, for browsers without WebGL2 in an OffscreenCanvas. */
export function createMainThreadBackend(): Backend | null {
  const canvas = document.createElement('canvas')
  const probed = probe(canvas, true)
  if (!probed.ok) return null
  const renderer = new EnhanceRenderer(canvas, probed.gl)
  let lost: ((reason: string) => void) | null = null
  let isLost = false
  canvas.addEventListener('webglcontextlost', (event) => {
    event.preventDefault()
    isLost = true
    lost?.('context lost')
  })
  return {
    async run(bitmap, preset, width, height) {
      try {
        if (isLost) throw new Error('context lost')
        await renderer.render(bitmap, preset, width, height)
        return await createImageBitmap(canvas)
      } finally {
        bitmap.close()
      }
    },
    onLost(cb) {
      lost = cb
    },
  }
}

import type { EnhancePreset } from './presets'

export interface JobMessage {
  type: 'job'
  id: number
  bitmap: ImageBitmap
  preset: EnhancePreset
  width: number
  height: number
}

export type WorkerMessage =
  | { type: 'ready'; supported: true }
  | { type: 'ready'; supported: false; reason: string }
  | { type: 'done'; id: number; bitmap: ImageBitmap }
  | { type: 'failed'; id: number; message: string }
  | { type: 'lost' }

/** What the queue drives: the worker, or the same renderer on a main-thread canvas. */
export interface Backend {
  run(bitmap: ImageBitmap, preset: EnhancePreset, width: number, height: number): Promise<ImageBitmap>
  /** Called once if the GPU context goes away outside a job. */
  onLost(cb: (reason: string) => void): void
}

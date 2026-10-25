import type { EnhancePreset } from './presets'
import type { Backend, JobMessage, WorkerMessage } from './protocol'

export type { EnhancePreset } from './presets'

export type EnhanceErrorCode = 'unsupported' | 'broken' | 'aborted'

/** Every rejection from the engine. On `unsupported` or `broken` the reader shows plain pages. */
export class EnhanceError extends Error {
  readonly code: EnhanceErrorCode
  constructor(code: EnhanceErrorCode, message?: string) {
    super(message ?? code)
    this.name = 'EnhanceError'
    this.code = code
  }
}

export interface EnhanceRequest {
  preset: EnhancePreset
  /** Device pixels: displayed CSS size times devicePixelRatio. Capped at 2x source per axis. */
  targetWidth: number
  targetHeight: number
  /**
   * Cache key, e.g. page URL plus region. With a key the result stays in the engine's LRU and is
   * owned by it: draw it (2D `drawImage`), never close or transfer it. Without one the caller owns it.
   */
  key?: string
  /** Lower runs first: 0 for the current page, its distance in reading order for the rest. */
  priority?: number
  signal?: AbortSignal
}

export interface EnhanceEngine {
  readonly supported: boolean
  /** True once a failure (context lost, compile error, out of memory) disabled the engine for the session. */
  readonly broken: boolean
  /** Takes ownership of `source` and closes it. Rejects with an `EnhanceError`. */
  enhance(source: ImageBitmap, request: EnhanceRequest): Promise<ImageBitmap>
  /** The cached result for `key` at this preset and target size, if any. */
  peek(key: string, preset: EnhancePreset, targetWidth: number, targetHeight: number): ImageBitmap | undefined
  setPriority(key: string, priority: number): void
  evict(key: string): void
  clear(): void
}

interface Waiter {
  resolve: (bitmap: ImageBitmap) => void
  reject: (error: EnhanceError) => void
  signal?: AbortSignal
  onAbort?: () => void
}

interface Job {
  key?: string
  preset: EnhancePreset
  w: number
  h: number
  priority: number
  seq: number
  source: ImageBitmap
  waiters: Waiter[]
}

interface Entry {
  bitmap: ImageBitmap
  preset: EnhancePreset
  w: number
  h: number
}

const pixelsOf = (b: ImageBitmap) => b.width * b.height

function defaultBudget(): number {
  const memory = (navigator as { deviceMemory?: number }).deviceMemory
  const coarse = typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches
  const pages = coarse || (memory !== undefined && memory <= 4) ? 4 : 8
  const dpr = globalThis.devicePixelRatio || 1
  return pages * Math.round(screen.width * dpr) * Math.round(screen.height * dpr)
}

class Engine implements EnhanceEngine {
  readonly supported: boolean
  broken = false
  private readonly backend: Backend | null
  private readonly budget: number
  private readonly queue: Job[] = []
  private readonly cache = new Map<string, Entry>()
  private running: Job | null = null
  private seq = 0

  constructor(backend: Backend | null, budget: number) {
    this.backend = backend
    this.supported = backend !== null
    this.budget = budget
    backend?.onLost((reason) => this.fail(reason))
  }

  enhance(source: ImageBitmap, request: EnhanceRequest): Promise<ImageBitmap> {
    const { preset, key, signal } = request
    const w = Math.round(request.targetWidth)
    const h = Math.round(request.targetHeight)
    const priority = request.priority ?? 0
    if (!this.supported || this.broken || signal?.aborted) {
      source.close()
      return Promise.reject(new EnhanceError(!this.supported ? 'unsupported' : this.broken ? 'broken' : 'aborted'))
    }
    if (key !== undefined) {
      const hit = this.peek(key, preset, w, h)
      if (hit) {
        source.close()
        return Promise.resolve(hit)
      }
    }
    return new Promise((resolve, reject) => {
      const waiter: Waiter = { resolve, reject, signal }
      const same = key === undefined ? undefined : [this.running, ...this.queue].find((j) => j && j.key === key && j.preset === preset && j.w === w && j.h === h)
      let job = same ?? undefined
      if (job) {
        source.close()
        job.priority = Math.min(job.priority, priority)
        job.waiters.push(waiter)
      } else {
        job = { key, preset, w, h, priority, seq: this.seq++, source, waiters: [waiter] }
        this.queue.push(job)
      }
      if (signal) {
        const owner = job
        waiter.onAbort = () => this.abandon(owner, waiter)
        signal.addEventListener('abort', waiter.onAbort, { once: true })
      }
      this.pump()
    })
  }

  peek(key: string, preset: EnhancePreset, targetWidth: number, targetHeight: number) {
    const e = this.cache.get(key)
    if (!e || e.preset !== preset || e.w !== Math.round(targetWidth) || e.h !== Math.round(targetHeight)) return undefined
    this.cache.delete(key)
    this.cache.set(key, e)
    return e.bitmap
  }

  setPriority(key: string, priority: number) {
    for (const j of this.queue) if (j.key === key) j.priority = priority
  }

  evict(key: string) {
    this.cache.get(key)?.bitmap.close()
    this.cache.delete(key)
  }

  clear() {
    for (const e of this.cache.values()) e.bitmap.close()
    this.cache.clear()
  }

  private abandon(job: Job, waiter: Waiter) {
    job.waiters = job.waiters.filter((w) => w !== waiter)
    waiter.reject(new EnhanceError('aborted'))
    if (job.waiters.length || job === this.running) return
    const i = this.queue.indexOf(job)
    if (i >= 0) this.queue.splice(i, 1)
    job.source.close()
  }

  private settle(job: Job, outcome: { bitmap: ImageBitmap } | { error: EnhanceError }) {
    for (const w of job.waiters) {
      if (w.onAbort) w.signal?.removeEventListener('abort', w.onAbort)
      if ('bitmap' in outcome) w.resolve(outcome.bitmap)
      else w.reject(outcome.error)
    }
    job.waiters = []
  }

  private store(key: string, entry: Entry) {
    this.evict(key)
    this.cache.set(key, entry)
    let total = 0
    for (const e of this.cache.values()) total += pixelsOf(e.bitmap)
    for (const [k, e] of this.cache) {
      if (total <= this.budget || k === key) break
      total -= pixelsOf(e.bitmap)
      this.evict(k)
    }
  }

  private pump() {
    if (this.running || this.broken || !this.backend || !this.queue.length) return
    let next = 0
    for (let i = 1; i < this.queue.length; i++) {
      const a = this.queue[i]
      const b = this.queue[next]
      if (a.priority < b.priority || (a.priority === b.priority && a.seq < b.seq)) next = i
    }
    const job = this.queue.splice(next, 1)[0]
    this.running = job
    this.backend.run(job.source, job.preset, job.w, job.h).then(
      (bitmap) => {
        this.running = null
        if (this.broken) {
          bitmap.close()
          return
        }
        // Every waiter gave up while it ran, so its scope has already let go of the key.
        if (!job.waiters.length) bitmap.close()
        else if (job.key !== undefined) this.store(job.key, { bitmap, preset: job.preset, w: job.w, h: job.h })
        this.settle(job, { bitmap })
        this.pump()
      },
      (error: unknown) => {
        this.running = null
        this.fail(error instanceof Error ? error.message : String(error))
        this.settle(job, { error: new EnhanceError('broken') })
      },
    )
  }

  private fail(reason: string) {
    if (this.broken) return
    this.broken = true
    console.debug('[enhance] disabled for this session:', reason)
    const error = new EnhanceError('broken', reason)
    for (const job of this.queue.splice(0)) {
      job.source.close()
      this.settle(job, { error })
    }
    if (this.running) this.settle(this.running, { error })
    this.clear()
  }
}

const READY_TIMEOUT_MS = 10_000

function createWorkerBackend(): Promise<Backend | null> {
  if (typeof Worker === 'undefined') return Promise.resolve(null)
  let worker: Worker
  try {
    worker = new Worker(new URL('./enhance.worker.ts', import.meta.url), { type: 'module' })
  } catch {
    return Promise.resolve(null)
  }
  return new Promise((resolve) => {
    const pending = new Map<number, { resolve: (b: ImageBitmap) => void; reject: (e: Error) => void }>()
    let nextId = 0
    let lost: ((reason: string) => void) | null = null
    let ready = false
    const die = (reason: string) => {
      for (const p of pending.values()) p.reject(new Error(reason))
      pending.clear()
      worker.terminate()
      if (ready) lost?.(reason)
      else resolve(null)
    }
    const timer = setTimeout(() => die('worker did not start'), READY_TIMEOUT_MS)
    worker.addEventListener('error', (event) => {
      event.preventDefault()
      clearTimeout(timer)
      die(event.message || 'worker error')
    })
    worker.addEventListener('messageerror', () => die('worker message error'))
    worker.addEventListener('message', (event: MessageEvent<WorkerMessage>) => {
      const msg = event.data
      if (msg.type === 'ready') {
        clearTimeout(timer)
        if (!msg.supported) {
          worker.terminate()
          resolve(null)
          return
        }
        ready = true
        resolve({
          run(bitmap, preset, width, height) {
            return new Promise((res, rej) => {
              const id = nextId++
              pending.set(id, { resolve: res, reject: rej })
              const job: JobMessage = { type: 'job', id, bitmap, preset, width, height }
              worker.postMessage(job, [bitmap])
            })
          },
          onLost(cb) {
            lost = cb
          },
        })
      } else if (msg.type === 'done' || msg.type === 'failed') {
        const p = pending.get(msg.id)
        pending.delete(msg.id)
        if (msg.type === 'done') {
          if (p) p.resolve(msg.bitmap)
          else msg.bitmap.close()
        } else p?.reject(new Error(msg.message))
      } else if (msg.type === 'lost') {
        die('context lost')
      }
    })
  })
}

async function createEngine(budget?: number): Promise<EnhanceEngine> {
  let backend = await createWorkerBackend()
  if (!backend && typeof document !== 'undefined') {
    const { createMainThreadBackend } = await import('./mainThread')
    try {
      backend = createMainThreadBackend()
    } catch {
      backend = null
    }
  }
  return new Engine(backend, budget ?? defaultBudget())
}

let engine: Promise<EnhanceEngine> | null = null

/** The session's engine, created on first call. Import this module with `import()` so it loads lazily. */
export function loadEnhanceEngine(): Promise<EnhanceEngine> {
  engine ??= createEngine()
  return engine
}

/** `navigator.storage`, which some browsers ship without `persist` and plain HTTP lacks entirely. */

export interface StorageEstimate {
  usage: number
  quota: number
}

export async function estimate(): Promise<StorageEstimate | null> {
  try {
    if (typeof navigator.storage?.estimate !== 'function') return null
    const { usage, quota } = await navigator.storage.estimate()
    return { usage: usage ?? 0, quota: quota ?? 0 }
  } catch {
    return null
  }
}

/** Whether the browser has agreed not to evict this origin's storage. Null when it cannot say. */
export async function isPersisted(): Promise<boolean | null> {
  try {
    if (typeof navigator.storage?.persisted !== 'function') return null
    return await navigator.storage.persisted()
  } catch {
    return null
  }
}

/** Asks for persistent storage. False when refused or not supported. */
export async function requestPersistence(): Promise<boolean> {
  try {
    if (typeof navigator.storage?.persist !== 'function') return false
    return await navigator.storage.persist()
  } catch {
    return false
  }
}

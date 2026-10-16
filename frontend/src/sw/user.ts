import { readSessionUser, writeSessionUser } from '../pwa/db'

/**
 * Who the page says is signed in. Read from IDB once per worker start (the browser can stop and
 * restart a worker at any time) and kept in memory after that. Null means nothing user-specific is
 * served from or stored in a cache.
 */
let known: Promise<number | null> | null = null

export function currentUser(): Promise<number | null> {
  known ??= readSessionUser().catch(() => null)
  return known
}

export async function setUser(userId: number | null): Promise<void> {
  const previous = known
  known = Promise.resolve(userId)
  if (previous && (await previous) === userId) return
  try {
    await writeSessionUser(userId)
  } catch {
    // Memory still holds it for this worker's life.
  }
}

/**
 * Page images the reader holds in memory as object URLs, so a turn to a page in the window (ahead,
 * behind, or the next chapter's first pages) never goes back to the browser's caches. Safari over
 * HTTPS sends every image request through the service worker and reused little of what the preload
 * had fetched, so turns there waited on the server even for the page just read.
 *
 * Least recently used first in `entries`, capped by bytes. Everything is dropped once no reader is
 * mounted.
 */

const MAX_BYTES = 96 * 1024 * 1024

interface Entry {
  objectUrl: string | null
  bytes: number
  pending: Promise<string | null>
}

const entries = new Map<string, Entry>()
let totalBytes = 0
let users = 0

function touch(src: string, entry: Entry) {
  entries.delete(src)
  entries.set(src, entry)
}

function trim() {
  for (const [src, entry] of entries) {
    if (totalBytes <= MAX_BYTES) return
    if (!entry.objectUrl) continue
    URL.revokeObjectURL(entry.objectUrl)
    totalBytes -= entry.bytes
    entries.delete(src)
  }
}

/** The in-memory copy of a page, if it has arrived. */
export function heldPageUrl(src: string): string | undefined {
  const entry = entries.get(src)
  if (!entry?.objectUrl) return undefined
  touch(src, entry)
  return entry.objectUrl
}

/**
 * Fetches a page into memory once. Resolves to its object URL, or null when the fetch failed, in
 * which case the page loads from its own URL as before.
 */
export function holdPage(src: string): Promise<string | null> {
  const known = entries.get(src)
  if (known) {
    touch(src, known)
    return known.pending
  }
  const entry: Entry = { objectUrl: null, bytes: 0, pending: Promise.resolve(null) }
  entry.pending = fetch(src, { credentials: 'same-origin' })
    .then((res) => (res.ok && res.headers.get('content-type')?.startsWith('image/') ? res.blob() : null))
    .then((blob) => {
      if (!blob || entries.get(src) !== entry) {
        if (entries.get(src) === entry) entries.delete(src)
        return null
      }
      entry.objectUrl = URL.createObjectURL(blob)
      entry.bytes = blob.size
      totalBytes += blob.size
      trim()
      return entry.objectUrl
    })
    .catch(() => {
      if (entries.get(src) === entry) entries.delete(src)
      return null
    })
  entries.set(src, entry)
  return entry.pending
}

/** Counts a mounted reader; the returned release drops every held page once the last one leaves. */
export function retainHeldPages(): () => void {
  users++
  return () => {
    users--
    if (users > 0) return
    for (const entry of entries.values()) if (entry.objectUrl) URL.revokeObjectURL(entry.objectUrl)
    entries.clear()
    totalBytes = 0
  }
}

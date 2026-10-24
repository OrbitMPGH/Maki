/**
 * The page's only door to Cache Storage. Every function tolerates `caches` being absent (plain
 * HTTP) and answers as if the cache were empty.
 */

function available(): boolean {
  return typeof caches !== 'undefined'
}

export async function cacheMatch(cacheName: string, url: string): Promise<Response | undefined> {
  if (!available()) return undefined
  const cache = await caches.open(cacheName)
  return cache.match(url, { ignoreVary: true })
}

export async function cacheHas(cacheName: string, url: string): Promise<boolean> {
  return (await cacheMatch(cacheName, url)) !== undefined
}

/** Throws what `put` throws, `QuotaExceededError` included, so a download can stop on it. */
export async function cachePut(cacheName: string, url: string, response: Response): Promise<void> {
  if (!available()) return
  const cache = await caches.open(cacheName)
  await cache.put(url, response)
}

export async function cacheDelete(cacheName: string, url: string): Promise<boolean> {
  if (!available()) return false
  const cache = await caches.open(cacheName)
  return cache.delete(url, { ignoreVary: true })
}

/** Every stored URL in a cache. */
export async function cacheUrls(cacheName: string): Promise<string[]> {
  if (!available() || !(await caches.has(cacheName))) return []
  const cache = await caches.open(cacheName)
  return (await cache.keys()).map((request) => request.url)
}

/** Deletes every entry whose URL passes `test`. Returns how many went. */
export async function cacheDeleteWhere(cacheName: string, test: (url: string) => boolean): Promise<number> {
  if (!available() || !(await caches.has(cacheName))) return 0
  const cache = await caches.open(cacheName)
  let deleted = 0
  for (const request of await cache.keys()) {
    if (test(request.url) && (await cache.delete(request))) deleted++
  }
  return deleted
}

export async function deleteCache(cacheName: string): Promise<boolean> {
  if (!available()) return false
  return caches.delete(cacheName)
}
